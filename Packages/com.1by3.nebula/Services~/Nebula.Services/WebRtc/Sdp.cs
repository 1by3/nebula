using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Nebula.WebRtc
{
    /// <summary>
    /// The SDP a data-channel-only peer connection exchanges. The gateway reads a browser's offer for its ICE
    /// credentials and certificate fingerprint, and answers as an ICE-lite, DTLS-passive endpoint with its host
    /// candidates listed, so the browser needs no STUN or TURN server and the gateway needs no trickle ICE.
    /// </summary>
    internal static class Sdp
    {
        public sealed class Description
        {
            public string IceUfrag, IcePwd, Fingerprint, Setup;
            public string Mid = "0";
            public int SctpPort = SctpAssociation.Port;
        }

        /// <summary>Read the application (data channel) section of an offer or answer.</summary>
        public static bool TryParse(string sdp, out Description description)
        {
            description = new Description();
            if (string.IsNullOrEmpty(sdp)) return false;
            bool sawApplication = false, inApplication = false, session = true;
            foreach (string raw in sdp.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("m=", StringComparison.Ordinal))
                {
                    session = false;
                    inApplication = !sawApplication && line.StartsWith("m=application", StringComparison.Ordinal) && line.Contains("webrtc-datachannel");
                    sawApplication |= inApplication;
                    continue;
                }
                if (!session && !inApplication) continue;
                if (TryValue(line, "a=ice-ufrag:", out string v)) description.IceUfrag = v;
                else if (TryValue(line, "a=ice-pwd:", out v)) description.IcePwd = v;
                else if (TryValue(line, "a=setup:", out v)) description.Setup = v;
                else if (TryValue(line, "a=fingerprint:", out v))
                {
                    int space = v.IndexOf(' ');
                    if (space > 0 && v.Substring(0, space).Equals("sha-256", StringComparison.OrdinalIgnoreCase)) description.Fingerprint = v.Substring(space + 1).Trim();
                }
                else if (inApplication && TryValue(line, "a=mid:", out v)) description.Mid = v;
                else if (inApplication && TryValue(line, "a=sctp-port:", out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) description.SctpPort = port;
            }
            return sawApplication && !string.IsNullOrEmpty(description.IceUfrag) && !string.IsNullOrEmpty(description.IcePwd) && !string.IsNullOrEmpty(description.Fingerprint);
        }

        public static string Answer(Description offer, string ufrag, string pwd, string fingerprint, IReadOnlyList<IPAddress> addresses, int port)
        {
            var b = new StringBuilder();
            string address = addresses.Count > 0 ? addresses[0].ToString() : "0.0.0.0";
            b.Append("v=0\r\n");
            b.Append("o=- ").Append(RandomNumberGenerator.GetInt32(int.MaxValue)).Append(" 1 IN IP4 0.0.0.0\r\n");
            b.Append("s=-\r\n");
            b.Append("t=0 0\r\n");
            b.Append("a=group:BUNDLE ").Append(offer.Mid).Append("\r\n");
            b.Append("a=ice-lite\r\n");
            b.Append("m=application ").Append(port).Append(" UDP/DTLS/SCTP webrtc-datachannel\r\n");
            b.Append("c=IN IP4 ").Append(address).Append("\r\n");
            b.Append("a=mid:").Append(offer.Mid).Append("\r\n");
            b.Append("a=ice-ufrag:").Append(ufrag).Append("\r\n");
            b.Append("a=ice-pwd:").Append(pwd).Append("\r\n");
            b.Append("a=fingerprint:sha-256 ").Append(fingerprint).Append("\r\n");
            b.Append("a=setup:passive\r\n");
            b.Append("a=sctp-port:").Append(SctpAssociation.Port).Append("\r\n");
            b.Append("a=max-message-size:").Append(SctpAssociation.MaxMessageSize).Append("\r\n");
            for (int i = 0; i < addresses.Count; i++)
                b.Append("a=candidate:").Append(i + 1).Append(" 1 udp ").Append(2130706431 - i).Append(' ').Append(addresses[i]).Append(' ').Append(port).Append(" typ host\r\n");
            b.Append("a=end-of-candidates\r\n");
            return b.ToString();
        }

        /// <summary>An ICE credential: letters and digits only (a subset of the ice-char set), so it never needs escaping.</summary>
        public static string RandomToken(int length)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return new string(chars);
        }

        private static bool TryValue(string line, string prefix, out string value)
        {
            value = null;
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
            value = line.Substring(prefix.Length).Trim();
            return true;
        }
    }
}
