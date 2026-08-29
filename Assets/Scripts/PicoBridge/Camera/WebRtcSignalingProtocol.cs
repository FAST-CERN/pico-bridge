using System;
using System.Collections.Generic;
using System.Text;

namespace PicoBridge.Camera
{
    /// <summary>
    /// Pure string logic of the teleimager HTTP signaling client: offer SDP
    /// candidate patching (vanilla ICE — the server has no trickle endpoint),
    /// POST body assembly and response parsing. No Unity dependencies so it
    /// can be unit-tested outside the editor (tools/SignalingProtocolTests).
    /// </summary>
    public static class WebRtcSignalingProtocol
    {
        /// <summary>
        /// Embed gathered ICE candidates into an offer SDP. teleimager's
        /// /offer endpoint answers once with all of its own candidates and
        /// offers no way to deliver client candidates later, so an offer
        /// without candidates can never connect. If the SDP already contains
        /// candidate lines (libwebrtc merged them during gathering) it is
        /// returned untouched.
        /// </summary>
        /// <param name="sdp">Local offer SDP.</param>
        /// <param name="candidateLines">
        /// Raw candidate payloads ("candidate:..."), e.g. from
        /// RTCIceCandidate.Candidate. Order is preserved; blank entries are
        /// skipped and a redundant "a=" prefix is tolerated.
        /// </param>
        public static string PatchSdpWithCandidates(string sdp, IReadOnlyList<string> candidateLines)
        {
            if (string.IsNullOrEmpty(sdp))
                return sdp;

            var usable = FilterCandidates(candidateLines);
            if (usable.Count == 0 || sdp.Contains("a=candidate:"))
                return sdp;

            string newline = sdp.Contains("\r\n") ? "\r\n" : "\n";
            var block = new StringBuilder();
            foreach (var candidate in usable)
                block.Append("a=").Append(candidate).Append(newline);
            block.Append("a=end-of-candidates").Append(newline);

            int insertAt = FindSecondMediaSectionStart(sdp, newline);
            if (insertAt < 0)
            {
                insertAt = sdp.Length;
                if (!sdp.EndsWith(newline, StringComparison.Ordinal))
                    block.Insert(0, newline);
            }

            return sdp.Substring(0, insertAt) + block + sdp.Substring(insertAt);
        }

        /// <summary>
        /// POST /offer request body: {"sdp":...,"type":"offer","codec":...}.
        /// aiohttp's request.json() requires a JSON body; a null codec makes
        /// the server fall back to its webrtc_codec config.
        /// </summary>
        public static string BuildOfferRequestBody(string sdp, string codec)
        {
            return "{\"sdp\":" + QuoteJson(sdp) +
                   ",\"type\":\"offer\"" +
                   ",\"codec\":" + (codec == null ? "null" : QuoteJson(codec)) + "}";
        }

        /// <summary>
        /// Pull a string field out of a small flat JSON document (e.g. the
        /// {"sdp":...,"type":"answer"} response). Same tolerant hand parser
        /// style as PicoBridgeManager/WebRtcCameraReceiver, but shared.
        /// </summary>
        public static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
                return string.Empty;

            string needle = "\"" + key + "\"";
            int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
            if (keyIndex < 0) return string.Empty;
            int colon = json.IndexOf(':', keyIndex + needle.Length);
            if (colon < 0) return string.Empty;
            int start = json.IndexOf('"', colon + 1);
            if (start < 0) return string.Empty;
            var result = new StringBuilder();
            bool escape = false;
            for (int i = start + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (escape)
                {
                    switch (c)
                    {
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case '\\': result.Append('\\'); break;
                        case '"': result.Append('"'); break;
                        case '/': result.Append('/'); break;
                        default: result.Append(c); break;
                    }
                    escape = false;
                }
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    return result.ToString();
                else
                    result.Append(c);
            }
            return string.Empty;
        }

        /// <summary>JSON-encode a string, escaping everything aiohttp's
        /// json parser could trip on (quotes, backslash, CRLF, control chars).</summary>
        public static string QuoteJson(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static List<string> FilterCandidates(IReadOnlyList<string> candidateLines)
        {
            var usable = new List<string>();
            if (candidateLines == null)
                return usable;
            foreach (var line in candidateLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var trimmed = line.Trim();
                if (trimmed.StartsWith("a=", StringComparison.Ordinal))
                    trimmed = trimmed.Substring(2);
                usable.Add(trimmed);
            }
            return usable;
        }

        /// <summary>Start offset of the second m= line, or -1 when there is only one media section.</summary>
        private static int FindSecondMediaSectionStart(string sdp, string newline)
        {
            int searchFrom = 0;
            bool seenFirst = false;
            while (searchFrom < sdp.Length)
            {
                int lineStart = sdp.IndexOf(newline + "m=", searchFrom, StringComparison.Ordinal);
                if (lineStart < 0)
                    return -1;
                if (seenFirst)
                    return lineStart + newline.Length;
                seenFirst = true;
                searchFrom = lineStart + newline.Length;
            }
            return -1;
        }
    }
}
