//
// AnnouncePeer.cs
//
// Authors:
//   Alan McGovern <alan.mcgovern@gmail.com>
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


using System.Collections.Generic;

using MonoTorrent.BEncoding;

namespace Controllarr.Core.Dht.Messages
{
    sealed class AnnouncePeer : QueryMessage
    {
        static readonly BEncodedString InfoHashKey = new BEncodedString ("info_hash");
        static readonly BEncodedString QueryName = new BEncodedString ("announce_peer");
        static readonly BEncodedString PortKey = new BEncodedString ("port");
        static readonly BEncodedString TokenKey = new BEncodedString ("token");

        internal NodeId InfoHash => new NodeId ((BEncodedString) Parameters[InfoHashKey]);

        internal BEncodedNumber Port => (BEncodedNumber) Parameters[PortKey];

        internal BEncodedString Token => (BEncodedString) Parameters[TokenKey];

        public AnnouncePeer (NodeId id, NodeId infoHash, BEncodedNumber port, BEncodedValue token)
            : base (id, QueryName)
        {
            Parameters.Add (InfoHashKey, BEncodedString.FromMemory (infoHash.AsMemory ()));
            Parameters.Add (PortKey, port);
            Parameters.Add (TokenKey, token);
        }

        public AnnouncePeer (BEncodedDictionary d)
            : base (d)
        {

        }

        public override ResponseMessage CreateResponse (BEncodedDictionary parameters)
        {
            return new AnnouncePeerResponse (parameters);
        }

        public override void Handle (DhtEngine engine, Node node)
        {
            base.Handle (engine, node);

            DhtMessage response;
            if (engine.TokenManager.VerifyToken (node, Token)) {
                long port = Parameters.TryGetValue (new BEncodedString ("implied_port"), out var implied) &&
                    implied is BEncodedNumber flag && flag.Number != 0 ? node.EndPoint.Port : Port.Number;
                if (port is < 1 or > 65535)
                    return;
                if (!engine.Torrents.TryGetValue (InfoHash, out var peers) && engine.Torrents.Count < 1024) {
                    peers = new List<Node> ();
                    engine.Torrents.Add (InfoHash, peers);
                }
                var endpoint = new System.Net.IPEndPoint (node.EndPoint.Address, (int) port);
                var existing = peers?.Find (peer => peer.EndPoint.Equals (endpoint));
                if (existing != null)
                    existing.Seen ();
                else if (peers != null && peers.Count < 64) {
                    var peer = new Node (node.Id, endpoint);
                    peer.Seen ();
                    peers.Add (peer);
                }
                response = new AnnouncePeerResponse (engine.RoutingTable.LocalNodeId, TransactionId);
            } else
                response = new ErrorMessage (TransactionId, ErrorCode.ProtocolError, "Invalid or expired token received");

            engine.MessageLoop.EnqueueSend (response, node, node.EndPoint);
        }
    }
}
