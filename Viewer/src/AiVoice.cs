using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// The microphone, through Windows' own offline speech recognizer (nothing is sent anywhere here).
    /// A small command grammar spots the names ("Bubble", the helpers' colours), yes/no and stop; a dictation
    /// grammar next to it catches whole sentences, so "Bubble, what's the weather" arrives as one phrase. Each
    /// phrase comes with its audio, which the crew sends to ElevenLabs for a proper transcript only when the
    /// phrase was meant for a bubble.
    /// </summary>
    sealed class Ears : IDisposable
    {
        public sealed class Phrase
        {
            public string Text;          // what Windows heard (rough)
            public bool Command;         // matched the command grammar (a bare name, yes/no, stop)
            public float Confidence;
            public byte[] Wav;           // the phrase's audio
        }

        public event Action<Phrase> Heard;
        public event Action SpeechStarted;   // someone started talking (before it's recognized)
        public volatile bool Muted;      // while a bubble is talking (so he doesn't hear himself)
        public long MutedUntilMs;
        public string Error;
        readonly SpeechRecognitionEngine m_Eng;
        readonly System.Diagnostics.Stopwatch m_Clock = System.Diagnostics.Stopwatch.StartNew();
        public long NowMs { get { return m_Clock.ElapsedMilliseconds; } }

        public static readonly string[] Names = new[] { "Bubble", "Hey Bubble" }.Concat(Bubble.HelperColors).ToArray();
        static readonly string[] s_Words = { "yes", "yeah", "no", "nope", "stop", "be quiet", "do it", "cancel" };

        public Ears()
        {
            try
            {
                RecognizerInfo info = null;
                foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
                    if (r.Culture.TwoLetterISOLanguageName == "en" && (info == null || r.Culture.Name == "en-US")) info = r;
                if (info == null) { Error = "No English Windows speech recognizer is installed (Settings > Time & language > Speech)"; return; }
                m_Eng = new SpeechRecognitionEngine(info);
                var all = new List<string>(Names); all.AddRange(s_Words);
                m_Eng.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(all.ToArray()))) { Name = "cmd", Weight = 1f, Priority = 1 });
                m_Eng.LoadGrammar(new DictationGrammar { Name = "dict", Weight = 0.6f });
                m_Eng.EndSilenceTimeout = TimeSpan.FromMilliseconds(700);
                m_Eng.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(900);
                m_Eng.BabbleTimeout = TimeSpan.Zero;
                m_Eng.SetInputToDefaultAudioDevice();
                m_Eng.SpeechDetected += (s, e) => { if (!Muted && NowMs >= MutedUntilMs) { var d = SpeechStarted; if (d != null) d(); } };
                m_Eng.SpeechRecognized += (s, e) => Deliver(e.Result, false);
                m_Eng.SpeechRecognitionRejected += (s, e) => Deliver(e.Result, true);
                m_Eng.RecognizeAsync(RecognizeMode.Multiple);
                Log.W("Ears: listening for \"Bubble\" with " + info.Description);
            }
            catch (Exception ex) { Error = "Microphone/speech recognizer unavailable: " + ex.Message; Log.W("Ears: " + Error); m_Eng = null; }
        }

        public bool Ok { get { return m_Eng != null; } }

        void Deliver(RecognitionResult r, bool rejected)
        {
            if (r == null || Muted || NowMs < MutedUntilMs) return;
            var p = new Phrase { Text = r.Text ?? "", Command = !rejected && r.Grammar != null && r.Grammar.Name == "cmd", Confidence = r.Confidence };
            try { if (r.Audio != null) { var ms = new MemoryStream(); r.Audio.WriteToWaveStream(ms); p.Wav = ms.ToArray(); } } catch { }
            var h = Heard; if (h != null) ThreadPool.QueueUserWorkItem(_ => { try { h(p); } catch (Exception ex) { Log.W("Ears handler: " + ex.Message); } });
        }

        public void Dispose()
        {
            if (m_Eng == null) return;
            try { m_Eng.RecognizeAsyncCancel(); m_Eng.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Speaks for the bubbles, one line at a time (ElevenLabs voice, played locally). While a line plays it
    /// exposes a loudness envelope so the speaking bubble's mouth moves with the words.
    /// </summary>
    sealed class Speaker : IDisposable
    {
        sealed class Line { public CrewAgent Agent; public string Text; public CancellationToken Ct; public Action After; }
        readonly BlockingCollection<Line> m_Q = new BlockingCollection<Line>();
        readonly Ears m_Ears;
        readonly Thread m_T;
        volatile bool m_Playing;
        volatile bool m_StopNow;
        public volatile string LastError;

        public Speaker(Ears ears)
        {
            m_Ears = ears;
            m_T = new Thread(Run) { IsBackground = true, Name = "speaker" }; m_T.Start();
        }

        /// <summary>Queues a line. 'after' runs once it has been fully spoken (not if it was hushed or cancelled).</summary>
        public void Say(CrewAgent a, string text, CancellationToken ct, Action after = null) { if (!string.IsNullOrWhiteSpace(text)) m_Q.Add(new Line { Agent = a, Text = text.Trim(), Ct = ct, After = after }); }

        void Done(Line line)
        {
            if (line.After == null || m_StopNow || line.Ct.IsCancellationRequested || m_Q.Count > 0) return;
            try { line.After(); } catch (Exception ex) { Log.W("Speaker after: " + ex.Message); }
        }

        /// <summary>Stops what's playing and drops everything queued.</summary>
        public void Hush()
        {
            Line l; while (m_Q.TryTake(out l)) { }
            m_StopNow = true;
        }

        /// <summary>Silences one bubble: stops it mid-word if it's the one talking, and drops its queued lines (others keep theirs).</summary>
        public void Hush(CrewAgent a)
        {
            var keep = new List<Line>(); Line l;
            while (m_Q.TryTake(out l)) if (l.Agent != a) keep.Add(l);
            foreach (var k in keep) m_Q.Add(k);
            if (m_Current == a) m_StopNow = true;
        }
        volatile CrewAgent m_Current;

        public bool Busy { get { return m_Q.Count > 0 || m_Playing; } }

        void Run()
        {
            foreach (var line in m_Q.GetConsumingEnumerable())
            {
                if (line.Ct.IsCancellationRequested) continue;
                m_StopNow = false;
                var a = line.Agent; m_Current = a;
                if (a.Gone) continue;
                a.Card = ElevenLabs.StripTags(line.Text); a.CardUntilMs = long.MaxValue;
                if (ElevenLabs.HasTags(line.Text)) Log.W(a.Name + " says (with feeling): " + line.Text);
                short[] pcm = null;
                if (Secrets.ElevenLabs != null)
                {
                    try { pcm = ElevenLabs.Speak(line.Text, a.VoiceId ?? CrewAgent.FallbackVoice, line.Ct); LastError = null; }
                    catch (OperationCanceledException) { continue; }
                    catch (Exception ex)
                    {
                        LastError = ex.Message; Log.W("Speaker: " + ex.Message);
                        // voice not available on this account: fall back to a stock voice from now on
                        if (a.VoiceId != null && a.VoiceId != CrewAgent.FallbackVoice)
                        {
                            a.VoiceId = CrewAgent.FallbackVoice;
                            try { pcm = ElevenLabs.Speak(line.Text, a.VoiceId, line.Ct); LastError = null; }
                            catch (OperationCanceledException) { continue; }
                            catch (Exception ex2) { LastError = ex2.Message; Log.W("Speaker: " + ex2.Message); }
                        }
                    }
                }
                if (m_StopNow || line.Ct.IsCancellationRequested) { a.CardUntilMs = m_Ears.NowMs + 1500; continue; }
                if (pcm == null || pcm.Length == 0)
                {
                    // no voice: just show the words for a while
                    a.CardUntilMs = m_Ears.NowMs + 1500 + line.Text.Length * 55;
                    Thread.Sleep(Math.Min(6000, 600 + line.Text.Length * 40));
                    Done(line);
                    continue;
                }
                // mouth envelope: loudness per 1/30 s, 0..1
                int step = ElevenLabs.Rate / 30; var env = new float[pcm.Length / step + 1]; float peak = 1;
                for (int i = 0; i < env.Length; i++)
                {
                    double s = 0; int n = 0;
                    for (int k = i * step; k < Math.Min(pcm.Length, (i + 1) * step); k++) { s += (double)pcm[k] * pcm[k]; n++; }
                    env[i] = n > 0 ? (float)Math.Sqrt(s / n) : 0; if (env[i] > peak) peak = env[i];
                }
                for (int i = 0; i < env.Length; i++) env[i] = Math.Min(1, env[i] / (peak * 0.6f));
                m_Ears.Muted = true;
                try
                {
                    m_Playing = true;
                    // its own audio stream: pops and other sounds mix with it, and a hush or pop stops it at once
                    // (in stereo from where his bubble floats, following your head while he talks)
                    AudioOut.Play(pcm, ElevenLabs.Rate, a.Spot, () => m_StopNow || a.Gone || line.Ct.IsCancellationRequested,
                                  () => { a.Envelope = env; a.SpeakStartMs = m_Ears.NowMs; a.Speaking = true; });
                }
                catch (Exception ex) { Log.W("Speaker playback: " + ex.Message); }
                finally
                {
                    m_Playing = false; a.Speaking = false; a.Envelope = null;
                    a.CardUntilMs = m_Ears.NowMs + 2500;
                    m_Ears.MutedUntilMs = m_Ears.NowMs + 400; m_Ears.Muted = false;   // the tail of our own voice
                }
                Done(line);
            }
        }

        public void Dispose() { Hush(); m_Q.CompleteAdding(); }
    }
}
