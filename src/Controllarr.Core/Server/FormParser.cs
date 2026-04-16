using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Controllarr.Core.Server
{
    // ────────────────────────────────────────────────────────────────
    // Parsed multipart section
    // ────────────────────────────────────────────────────────────────

    public sealed class MultipartSection
    {
        public string Name { get; set; } = string.Empty;
        public string? FileName { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    // ────────────────────────────────────────────────────────────────
    // Form / query / multipart parsing utilities
    // ────────────────────────────────────────────────────────────────

    public static class FormParser
    {
        /// <summary>
        /// Reads a URL-encoded form body and returns key-value pairs.
        /// Keys that appear more than once keep only the last value.
        /// </summary>
        public static async Task<Dictionary<string, string>> ParseForm(HttpRequest request)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            request.Body.Position = 0; // rewind if already partially read
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            string body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body))
                return result;

            // Split on '&', then on '='
            foreach (var segment in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = segment.IndexOf('=');
                if (eq < 0)
                {
                    result[WebUtility.UrlDecode(segment)] = string.Empty;
                }
                else
                {
                    string key = WebUtility.UrlDecode(segment[..eq]);
                    string val = WebUtility.UrlDecode(segment[(eq + 1)..]);
                    result[key] = val;
                }
            }

            return result;
        }

        /// <summary>
        /// Parses a raw query string (with or without leading '?') into
        /// key-value pairs.
        /// </summary>
        public static Dictionary<string, string> ParseQuery(string? queryString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(queryString))
                return result;

            // Strip leading '?'
            string raw = queryString.StartsWith('?') ? queryString[1..] : queryString;

            foreach (var segment in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = segment.IndexOf('=');
                if (eq < 0)
                {
                    result[WebUtility.UrlDecode(segment)] = string.Empty;
                }
                else
                {
                    string key = WebUtility.UrlDecode(segment[..eq]);
                    string val = WebUtility.UrlDecode(segment[(eq + 1)..]);
                    result[key] = val;
                }
            }

            return result;
        }

        /// <summary>
        /// Reads a multipart/form-data body and returns each section with
        /// its name, optional file name, and raw byte data.
        /// </summary>
        public static async Task<List<MultipartSection>> ParseMultipart(HttpRequest request)
        {
            var sections = new List<MultipartSection>();

            string? contentType = request.ContentType;
            if (string.IsNullOrEmpty(contentType))
                return sections;

            var mediaType = MediaTypeHeaderValue.Parse(contentType);
            string? boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;

            if (string.IsNullOrWhiteSpace(boundary))
                return sections;

            request.Body.Position = 0;
            var multipartReader = new MultipartReader(boundary, request.Body);

            Microsoft.AspNetCore.WebUtilities.MultipartSection? section;
            while ((section = await multipartReader.ReadNextSectionAsync()) != null)
            {
                var parsed = new MultipartSection();

                // Extract content-disposition header for name / filename
                if (section.Headers != null &&
                    section.Headers.TryGetValue("Content-Disposition", out var cdValues))
                {
                    string cd = cdValues.ToString();
                    parsed.Name = ExtractHeaderParam(cd, "name");
                    parsed.FileName = ExtractHeaderParam(cd, "filename");
                    if (string.IsNullOrEmpty(parsed.FileName))
                        parsed.FileName = null;
                }

                using var ms = new MemoryStream();
                await section.Body.CopyToAsync(ms);
                parsed.Data = ms.ToArray();

                sections.Add(parsed);
            }

            return sections;
        }

        // ── Helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Extracts a quoted parameter value from a Content-Disposition header.
        /// Example: name="urls" -> "urls"
        /// </summary>
        private static string ExtractHeaderParam(string header, string paramName)
        {
            // Look for paramName="value" (with or without quotes)
            string search = paramName + "=";
            int idx = header.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return string.Empty;

            int start = idx + search.Length;
            if (start >= header.Length)
                return string.Empty;

            if (header[start] == '"')
            {
                // Quoted value
                start++;
                int end = header.IndexOf('"', start);
                return end < 0 ? header[start..] : header[start..end];
            }
            else
            {
                // Unquoted – ends at ';' or end of string
                int end = header.IndexOf(';', start);
                return end < 0 ? header[start..].Trim() : header[start..end].Trim();
            }
        }
    }
}
