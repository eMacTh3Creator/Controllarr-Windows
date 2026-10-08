//
// DhtEngine.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2008 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using MonoTorrent;
using MonoTorrent.Dht;
using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections.Dht;
using Controllarr.Core.Dht.Messages;
using Controllarr.Core.Dht.Tasks;

namespace Controllarr.Core.Dht
{
    enum ErrorCode
    {
        GenericError = 201,
        ServerError = 202,
        ProtocolError = 203,// malformed packet, invalid arguments, or bad token
        MethodUnknown = 204//Method Unknown
    }

    class TransferMonitor : ITransferMonitor
    {
        long ITransferMonitor.UploadRate => SendMonitor.Rate;
        long ITransferMonitor.DownloadRate => ReceiveMonitor.Rate;
        long ITransferMonitor.BytesSent => SendMonitor.Total;
        long ITransferMonitor.BytesReceived => ReceiveMonitor.Total;

        internal SpeedMonitor SendMonitor { get; } = new SpeedMonitor ();
        internal SpeedMonitor ReceiveMonitor { get; } = new SpeedMonitor ();
    }

    public class DhtEngine : IDisposable, IDhtEngine
    {
        static readonly TimeSpan DefaultAnnounceInternal = TimeSpan.FromMinutes (10);
        static readonly TimeSpan DefaultMinimumAnnounceInterval = TimeSpan.FromMinutes (3);

        #region Events

        public event EventHandler<PeersFoundEventArgs>? PeersFound;
        public event EventHandler? StateChanged;

        #endregion Events

        internal static MainLoop MainLoop { get; } = new MainLoop ("DhtLoop");

        // IPV6 - create an IPV4 and an IPV6 dht engine
        public AddressFamily AddressFamily { get; private set; } = AddressFamily.InterNetwork;

        public TimeSpan AnnounceInterval => DefaultAnnounceInternal;

        public bool Disposed { get; private set; }

        public ITransferMonitor Monitor { get; }

        public TimeSpan MinimumAnnounceInterval => DefaultMinimumAnnounceInterval;

        public DhtState State { get; private set; }

        internal TimeSpan BucketRefreshTimeout { get; set; }
        internal NodeId LocalId => RoutingTable.LocalNodeId;
        internal MessageLoop MessageLoop { get; }
        public int NodeCount => RoutingTable.CountNodes ();
        IEnumerable<Node> PendingNodes { get; set; }
        internal RoutingTable RoutingTable { get; }
        internal TokenManager TokenManager { get; }
        internal Dictionary<NodeId, List<Node>> Torrents { get; }

        internal Func<System.Threading.CancellationToken, Task<IReadOnlyList<IPEndPoint>>> BootstrapResolver { get; }
        System.Threading.CancellationTokenSource bootstrapCancellation = new System.Threading.CancellationTokenSource ();
        internal System.Threading.CancellationToken BootstrapCancellation => bootstrapCancellation.Token;
        bool running;
        int generation;
        readonly HashSet<InfoHash> peerLookups = new HashSet<InfoHash> ();
        readonly HashSet<InfoHash> announcements = new HashSet<InfoHash> ();

        public DhtEngine (Func<System.Threading.CancellationToken, Task<IReadOnlyList<IPEndPoint>>> bootstrapResolver)
        {
            BootstrapResolver = bootstrapResolver ?? throw new ArgumentNullException (nameof (bootstrapResolver));
            var monitor = new TransferMonitor ();
            BucketRefreshTimeout = TimeSpan.FromMinutes (15);
            MessageLoop = new MessageLoop (this, monitor);
            Monitor = monitor;
            PendingNodes = Array.Empty<Node> ();
            RoutingTable = new RoutingTable ();
            State = DhtState.NotReady;
            TokenManager = new TokenManager ();
            Torrents = new Dictionary<NodeId, List<Node>> ();

            MainLoop.QueueTimeout (TimeSpan.FromMinutes (5), () => {
                if (!Disposed) {
                    TokenManager.RefreshTokens ();
                    foreach (var hash in Torrents.Keys.ToArray ()) {
                        Torrents[hash].RemoveAll (node => node.LastSeen > TimeSpan.FromMinutes (30));
                        if (Torrents[hash].Count == 0)
                            Torrents.Remove (hash);
                    }
                }
                return !Disposed;
            });
            MainLoop.QueueTimeout (TimeSpan.FromSeconds (30), () => {
                if (Disposed)
                    return false;
                if (running) {
                    if (State == DhtState.NotReady) {
                        RaiseStateChanged (DhtState.Initialising);
                        InitializeAsync (Array.Empty<Node> (), generation);
                    } else if (State == DhtState.Ready) {
                        _ = RefreshBuckets ();
                    }
                }
                return true;
            });
        }

        public async void Add (IEnumerable<ReadOnlyMemory<byte>> nodes)
        {
            try {
                await MainLoop;
                if (Disposed) return;
                var supplied = Node.FromCompactNode (nodes).Take (256).ToArray ();
                if (State == DhtState.NotReady) {
                    PendingNodes = supplied;
                } else {
                    foreach (var node in supplied) {
                        if (!running || Disposed) return;
                        try { await Add (node); }
                        catch { /* Invalid/unreachable metadata nodes are optional hints. */ }
                    }
                }
            } catch { /* Malformed metadata must not escape an async-void callback. */ }
        }

        internal async Task Add (Node node)
            => await SendQueryAsync (new Ping (RoutingTable.LocalNodeId), node);

        public async void Announce (InfoHash infoHash, int port)
        {
            CheckDisposed ();
            if (infoHash is null)
                throw new ArgumentNullException (nameof (infoHash));

            try {
                await MainLoop;
                var task = new AnnounceTask (this, infoHash, port);
                if (!running || announcements.Count >= 64 || !announcements.Add (infoHash))
                    return;
                try { await task.ExecuteAsync (); }
                finally { announcements.Remove (infoHash); }
            } catch {
                // Ignore?
            }
        }

        void CheckDisposed ()
        {
            if (Disposed)
                throw new ObjectDisposedException (GetType ().Name);
        }

        public void Dispose ()
        {
            if (Disposed)
                return;

            // Ensure we don't break any threads actively running right now
            MainLoop.QueueWait (() => {
                if (Disposed) return;
                running = false;
                generation++;
                bootstrapCancellation.Cancel ();
                bootstrapCancellation.Dispose ();
                MessageLoop.Stop ();
                Disposed = true;
            });
        }

        public async void GetPeers (InfoHash infoHash)
        {
            CheckDisposed ();
            if (infoHash == null)
                throw new ArgumentNullException (nameof (infoHash));

            try {
                await MainLoop;
                var task = new GetPeersTask (this, infoHash);
                if (!running || peerLookups.Count >= 64 || !peerLookups.Add (infoHash))
                    return;
                try { await task.ExecuteAsync (); }
                finally { peerLookups.Remove (infoHash); }
            } catch {
                // Ignore?
            }
        }

        async void InitializeAsync (IEnumerable<Node> nodes, int version)
        {
            try {
                await MainLoop;
                await new InitialiseTask (this, nodes).ExecuteAsync ();
            } catch {
                // Bootstrap failures remain retryable and never use system DNS.
            } finally {
                await MainLoop;
                if (!Disposed && running && generation == version)
                    RaiseStateChanged (RoutingTable.NeedsBootstrap ? DhtState.NotReady : DhtState.Ready);
            }
        }

        internal void RaisePeersFound (NodeId infoHash, IList<PeerInfo> peers)
        {
            PeersFound?.Invoke (this, new PeersFoundEventArgs (InfoHash.FromMemory (infoHash.AsMemory ()), peers));
        }

        void RaiseStateChanged (DhtState newState)
        {
            if (State != newState) {
                State = newState;
                StateChanged?.Invoke (this, EventArgs.Empty);
            }
        }

        internal async Task RefreshBuckets ()
        {
            await MainLoop;

            var refreshTasks = new List<Task> ();
            foreach (Bucket b in RoutingTable.Buckets) {
                if (b.LastChanged > BucketRefreshTimeout) {
                    b.Changed ();
                    var task = new RefreshBucketTask (this, b);
                    refreshTasks.Add (task.Execute ());
                }
            }

            if (refreshTasks.Count > 0)
                await Task.WhenAll (refreshTasks).ConfigureAwait (false);
        }

        public async Task<ReadOnlyMemory<byte>> SaveNodesAsync ()
        {
            await MainLoop;

            var details = new BEncodedList ();

            foreach (Bucket b in RoutingTable.Buckets) {
                foreach (Node n in b.Nodes)
                    if (n.State != NodeState.Bad)
                        details.Add (n.CompactNode ());

                if (b.Replacement != null)
                    if (b.Replacement.State != NodeState.Bad)
                        details.Add (b.Replacement.CompactNode ());
            }

            return details.Encode ();
        }

        internal async Task<SendQueryEventArgs> SendQueryAsync (QueryMessage query, Node node)
        {
            await MainLoop;

            var e = default (SendQueryEventArgs);
            for (int i = 0; i < 4; i++) {
                e = await MessageLoop.SendAsync (query, node);
                if (!running || Disposed)
                    return e;

                // If the message timed out and we we haven't already hit the maximum retries
                // send again. Otherwise we propagate the eventargs through the Complete event.
                if (e.TimedOut) {
                    node.FailedCount++;
                    continue;
                } else {
                    node.Seen ();
                    return e;
                }
            }

            return e;
        }

        public Task StartAsync ()
            => StartAsync (ReadOnlyMemory<byte>.Empty);

        public Task StartAsync (ReadOnlyMemory<byte> initialNodes)
        {
            IEnumerable<Node> cached = Array.Empty<Node> ();
            try {
                if (!initialNodes.IsEmpty) {
                    var list = BEncodedValue.Decode<BEncodedList> (initialNodes.Span);
                    cached = Node.FromCompactNode (list.OfType<BEncodedString> ().Select (s => s.AsMemory ())).Take (256).ToArray ();
                }
            } catch {
                // Old or corrupt caches must not prevent fresh, bound bootstrap.
            }
            return StartAsync (cached.Concat (PendingNodes).Take (256));
        }

        async Task StartAsync (IEnumerable<Node> nodes)
        {
            CheckDisposed ();

            await MainLoop;
            if (running)
                return;
            running = true;
            generation++;
            bootstrapCancellation.Dispose ();
            bootstrapCancellation = new System.Threading.CancellationTokenSource ();
            MessageLoop.Start ();
            if (RoutingTable.NeedsBootstrap) {
                RaiseStateChanged (DhtState.Initialising);
                InitializeAsync (nodes, generation);
            } else {
                RaiseStateChanged (DhtState.Ready);
            }

        }

        public async Task StopAsync ()
        {
            await MainLoop;

            running = false;
            generation++;
            bootstrapCancellation.Cancel ();
            MessageLoop.Stop ();
            RaiseStateChanged (DhtState.NotReady);
        }

        internal async Task WaitForState (DhtState state)
        {
            await MainLoop;
            if (State == state)
                return;

            var tcs = new TaskCompletionSource<object> ();

            void handler (object? o, EventArgs e)
            {
                if (State == state) {
                    StateChanged -= handler;
                    tcs.SetResult (true);
                }
            }

            StateChanged += handler;
            await tcs.Task;
        }

        public async Task SetListenerAsync (IDhtListener listener)
        {
            await MainLoop;
            await MessageLoop.SetListener (listener);
        }
    }
}
