using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace XrealDesktop.PcViewer
{
    /// <summary>JSON via the framework's JavaScriptSerializer: objects are Dictionary&lt;string, object&gt;, arrays object[] / List.</summary>
    static class Json
    {
        static readonly JavaScriptSerializer s_J = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
        public static string Write(object o) { return s_J.Serialize(o); }
        public static Dictionary<string, object> Read(string s) { return s_J.DeserializeObject(s) as Dictionary<string, object>; }
        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        public static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null; }
        public static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v; if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return def; }
        }
        public static IEnumerable<object> Arr(object o)
        {
            var a = o as System.Collections.IEnumerable;
            if (a == null || o is string) yield break;
            foreach (var x in a) yield return x;
        }
    }

    /// <summary>
    /// API keys, encrypted with Windows DPAPI for this Windows user (%APPDATA%\AirBubble\keys.dat).
    /// The DPAPI entropy string "XrealPcViewer.keys" predates the AirBubble rename; keep it or saved keys can't be read.
    /// The environment variables ANTHROPIC_API_KEY / ELEVENLABS_API_KEY are used when nothing is saved.
    /// </summary>
    static class Secrets
    {
        static string PathFor() { return Path.Combine(Settings.Dir, "keys.dat"); }
        static Dictionary<string, object> Load()
        {
            try
            {
                var raw = ProtectedData.Unprotect(File.ReadAllBytes(PathFor()), Encoding.UTF8.GetBytes("XrealPcViewer.keys"), DataProtectionScope.CurrentUser);
                return Json.Read(Encoding.UTF8.GetString(raw)) ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }
        public static string Get(string name, string envVar)
        {
            string v = Json.Str(Load(), name);
            if (string.IsNullOrEmpty(v)) v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrEmpty(v) ? null : v.Trim();
        }
        public static void Set(string name, string value)
        {
            var d = Load();
            if (string.IsNullOrEmpty(value)) d.Remove(name); else d[name] = value.Trim();
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllBytes(PathFor(), ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(d)), Encoding.UTF8.GetBytes("XrealPcViewer.keys"), DataProtectionScope.CurrentUser));
        }
        public static string Anthropic { get { return Get("anthropic", "ANTHROPIC_API_KEY"); } }
        public static string ElevenLabs { get { return Get("elevenlabs", "ELEVENLABS_API_KEY"); } }
    }

    sealed class ApiException : Exception
    {
        public readonly int Status;
        public ApiException(int status, string msg) : base(msg) { Status = status; }
    }

    static class Http
    {
        static HttpClient s_Client;
        public static HttpClient Client
        {
            get
            {
                if (s_Client == null)
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    s_Client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                }
                return s_Client;
            }
        }
    }

    /// <summary>
    /// Claude Messages API over plain HTTPS (the viewer is built with the in-box .NET Framework compiler, so no SDK).
    /// Non-streaming: replies are short spoken sentences. Server-side refusal fallback is on.
    /// </summary>
    static class Claude
    {
        public const string Model = "claude-opus-5-5";
        const string Url = "https://api.anthropic.com/v1/messages";

        /// <summary>One Messages call. Retries 429 / 5xx / network errors twice with backoff.</summary>
        public static Dictionary<string, object> Send(object system, List<object> messages, List<object> tools, string effort, int maxTokens, CancellationToken ct)
        {
            string key = Secrets.Anthropic;
            if (key == null) throw new ApiException(401, "No Claude API key: add it in Settings (Ctrl+Alt+S) > Bubble AI.");
            var body = Json.Obj("model", Model, "max_tokens", maxTokens, "system", system, "messages", messages,
                                "thinking", Json.Obj("type", "adaptive"),
                                "output_config", Json.Obj("effort", effort),
                                "fallbacks", "default");
            if (tools != null && tools.Count > 0) body["tools"] = tools;
            string json = Json.Write(body);
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, Url))
                    {
                        req.Headers.Add("x-api-key", key);
                        req.Headers.Add("anthropic-version", "2026-10-21");
                        req.Headers.Add("anthropic-beta", "server-side-fallback-2026-07-01");
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (var resp = Http.Client.SendAsync(req, ct).Result)
                        {
                            string text = resp.Content.ReadAsStringAsync().Result;
                            int code = (int)resp.StatusCode;
                            if (code == 200) return Json.Read(text);
                            string msg = text;
                            try { var e = Json.Read(text); msg = Json.Str(e["error"] as Dictionary<string, object>, "message") ?? text; } catch { }
                            if ((code == 429 || code >= 500) && attempt < 2) { Thread.Sleep(1500 * (attempt + 1)); continue; }
                            throw new ApiException(code, "Claude API " + code + ": " + msg);
                        }
                    }
                }
                catch (AggregateException ae)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();
                    if (ae.InnerException is ApiException) throw ae.InnerException;
                    if (attempt < 2) { Thread.Sleep(1500 * (attempt + 1)); continue; }
                    throw new ApiException(0, "Couldn't reach Claude: " + ae.InnerException.Message);
                }
            }
        }
    }

    /// <summary>ElevenLabs: text to speech (PCM) and speech to text.</summary>
    static class ElevenLabs
    {
        const string Base = "https://api.elevenlabs.io";
        public const int Rate = 22050;
        public static string TtsModel = "eleven_flash_v2_5";

        static HttpRequestMessage Req(HttpMethod m, string url)
        {
            string key = Secrets.ElevenLabs;
            if (key == null) throw new ApiException(401, "No ElevenLabs API key: add it in Settings (Ctrl+Alt+S) > Bubble AI.");
            var r = new HttpRequestMessage(m, Base + url);
            r.Headers.Add("xi-api-key", key);
            return r;
        }

        static byte[] Run(HttpRequestMessage req, CancellationToken ct, out int code, out string err)
        {
            try
            {
                using (req)
                using (var resp = Http.Client.SendAsync(req, ct).Result)
                {
                    code = (int)resp.StatusCode;
                    var bytes = resp.Content.ReadAsByteArrayAsync().Result;
                    err = code == 200 ? null : Encoding.UTF8.GetString(bytes);
                    return bytes;
                }
            }
            catch (AggregateException ae)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                throw new ApiException(0, "Couldn't reach ElevenLabs: " + ae.InnerException.Message);
            }
        }

        /// <summary>Speech for 'text' as 16-bit mono PCM at Rate Hz.</summary>
        public const string ExpressiveModel = "eleven_v3";
        static bool s_NoExpressive;
        static readonly System.Text.RegularExpressions.Regex s_Tag = new System.Text.RegularExpressions.Regex(@"\[[A-Za-z][A-Za-z '\-]{0,30}\]\s*");

        /// <summary>True if the text has audio tags like [laughs] or [whispers].</summary>
        public static bool HasTags(string text) { return s_Tag.IsMatch(text ?? ""); }
        /// <summary>The text without audio tags (for the speech card, and for models that would read them out).</summary>
        public static string StripTags(string text) { return System.Text.RegularExpressions.Regex.Replace(s_Tag.Replace(text ?? "", ""), @"\s{2,}", " ").Trim(); }

        /// <summary>Speech for 'text' as 16-bit mono PCM at Rate Hz. Text with audio tags ([laughs], [sighs], [excited]...)
        /// goes to the expressive v3 model; plain text to the fast model.</summary>
        public static short[] Speak(string text, string voiceId, CancellationToken ct)
        {
            if (HasTags(text))
            {
                if (!s_NoExpressive)
                {
                    try { return SpeakWith(text, voiceId, ExpressiveModel, ct); }
                    catch (ApiException ex)
                    {
                        if (ex.Status != 400 && ex.Status != 403 && ex.Status != 422) throw;
                        Log.W("ElevenLabs: " + ExpressiveModel + " refused (" + ex.Status + "), speaking without emotion tags");
                        s_NoExpressive = true;
                    }
                }
                text = StripTags(text);
            }
            return SpeakWith(text, voiceId, null, ct);
        }

        static short[] SpeakWith(string text, string voiceId, string model, CancellationToken ct)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var req = Req(HttpMethod.Post, "/v1/text-to-speech/" + Uri.EscapeDataString(voiceId) + "?output_format=pcm_" + Rate);
                req.Content = new StringContent(Json.Write(Json.Obj("text", text, "model_id", model ?? TtsModel)), Encoding.UTF8, "application/json");
                int code; string err;
                var bytes = Run(req, ct, out code, out err);
                if (code == 200)
                {
                    var pcm = new short[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
                    return pcm;
                }
                // the fast model not available on this account: fall back to the default model once
                if (model == null && pass == 0 && (code == 400 || code == 422) && TtsModel != "eleven_multilingual_v2") { Log.W("ElevenLabs: " + TtsModel + " refused (" + code + "), using eleven_multilingual_v2"); TtsModel = "eleven_multilingual_v2"; continue; }
                throw new ApiException(code, "ElevenLabs speech " + code + ": " + Short(err));
            }
            return new short[0];
        }

        /// <summary>Transcribes a WAV file. Returns the text (may be empty).</summary>
        public static string Transcribe(byte[] wav, CancellationToken ct)
        {
            var req = Req(HttpMethod.Post, "/v1/speech-to-text");
            var form = new MultipartFormDataContent();
            form.Add(new StringContent("scribe_v2"), "model_id");
            var file = new ByteArrayContent(wav); file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            req.Content = form;
            int code; string err;
            var bytes = Run(req, ct, out code, out err);
            if (code != 200) throw new ApiException(code, "ElevenLabs transcription " + code + ": " + Short(err));
            return (Json.Str(Json.Read(Encoding.UTF8.GetString(bytes)), "text") ?? "").Trim();
        }

        public sealed class Voice { public string Id, Name, Description; }

        /// <summary>The account's premade/default voices (first 100).</summary>
        public static List<Voice> Voices(CancellationToken ct)
        {
            var req = Req(HttpMethod.Get, "/v2/voices?page_size=100");
            int code; string err;
            var bytes = Run(req, ct, out code, out err);
            if (code != 200) throw new ApiException(code, "ElevenLabs voices " + code + ": " + Short(err));
            var list = new List<Voice>();
            foreach (var o in Json.Arr(Json.Read(Encoding.UTF8.GetString(bytes))["voices"]))
            {
                var v = o as Dictionary<string, object>;
                if (v != null) list.Add(new Voice { Id = Json.Str(v, "voice_id"), Name = Json.Str(v, "name"), Description = Json.Str(v, "description") });
            }
            return list;
        }

        static string Short(string s) { if (s == null) return ""; return s.Length > 300 ? s.Substring(0, 300) + "..." : s; }

        public static byte[] Wav(short[] pcm, int rate)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(0x46464952); w.Write(36 + pcm.Length * 2); w.Write(0x45564157);
            w.Write(0x20746D66); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(0x61746164); w.Write(pcm.Length * 2);
            foreach (var s in pcm) w.Write(s);
            w.Flush(); return ms.ToArray();
        }
    }
}
