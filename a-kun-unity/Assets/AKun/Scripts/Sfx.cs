using System;
using System.Collections.Generic;
using UnityEngine;

namespace AKun
{
    // 効果音と BGM。音の素材ファイルは使わず、起動したときにコードで波形を作る (シーンと同じく全部コードで組み立てる)。
    //   一度だけ鳴らす音: Play("click", 位置)。位置を渡すとそこから聞こえる (立体音響)
    //   手の音: Hand(手, 位置, なでる, こねる, 伸ばす, 伸ばす高さ, 塗る)。毎フレーム呼ぶと、強さに合わせて音量がなめらかに変わる
    //   BGM: Mood("calm") / Mood("tense") / Mood("none")
    // 雰囲気は「ドロとケイのかくれんぼ」: 作る間は軽く、本番は緊張する音、見つかったらサイレン
    public static class Sfx
    {
        public static float sfxVolume = 0.8f, bgmVolume = 0.35f;

        static int SR = 48000;
        static GameObject root;
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        static AudioSource[] pool;
        static int poolNext;
        static readonly Dictionary<string, float> lastPlayed = new Dictionary<string, float>();

        // 手の音 (手ごとに、ずっと鳴らしておいて音量だけ変える)
        class Voice { public Transform t; public AudioSource rub, squish, stretch, brush; public float vr, vs, vt, vb; }
        static Voice[] voices;

        static AudioSource bgmCalm, bgmTense;
        static string mood = "calm";
        static float calmK = 1, tenseK;

        public static bool Ready => root != null;

        public static void Init(Transform listener)
        {
            if (root != null) return;
            if (listener != null && listener.GetComponent<AudioListener>() == null) listener.gameObject.AddComponent<AudioListener>();
            SR = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            root = new GameObject("Sfx");
            UnityEngine.Object.DontDestroyOnLoad(root);
            var rnd = new System.Random(7);
            try { BuildClips(rnd); }
            catch (Exception e) { Debug.LogWarning($"[clay] 効果音を作れません: {e.Message}"); }

            pool = new AudioSource[10];
            for (int i = 0; i < pool.Length; i++) pool[i] = Source($"oneshot{i}", null, false);

            voices = new Voice[2];
            for (int i = 0; i < 2; i++)
            {
                var t = new GameObject($"hand-voice{i}").transform;
                t.SetParent(root.transform, false);
                voices[i] = new Voice
                {
                    t = t,
                    rub = Loop(t, "rubLoop"),
                    squish = Loop(t, "squishLoop"),
                    stretch = Loop(t, "stretchLoop"),
                    brush = Loop(t, "brushLoop"),
                };
            }
            bgmCalm = Source("bgm-calm", Clip("bgmCalm"), true, 0);
            bgmTense = Source("bgm-tense", Clip("bgmTense"), true, 0);
            bgmCalm.volume = 0; bgmTense.volume = 0;
            if (bgmCalm.clip != null) bgmCalm.Play();
            if (bgmTense.clip != null) bgmTense.Play();
        }

        static AudioClip Clip(string name) => clips.TryGetValue(name, out var c) ? c : null;

        static AudioSource Source(string name, AudioClip clip, bool loop, float spatial = 0.85f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.clip = clip;
            s.spatialBlend = spatial;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 0.6f;
            s.maxDistance = 12f;
            s.dopplerLevel = 0;
            return s;
        }

        static AudioSource Loop(Transform parent, string clip)
        {
            var s = Source(clip, Clip(clip), true);
            s.transform.SetParent(parent, false);
            s.volume = 0;
            if (s.clip != null) s.Play();
            return s;
        }

        // ---------- 鳴らす ----------

        // 一度だけ鳴らす。同じ音を続けて鳴らしすぎないよう、minGap 秒の間はもう鳴らさない
        public static void Play(string name, Vector3? at = null, float vol = 1, float pitch = 1, float minGap = 0.03f)
        {
            if (root == null || !clips.TryGetValue(name, out var clip)) return;
            float now = Time.unscaledTime;
            if (lastPlayed.TryGetValue(name, out var last) && now - last < minGap) return;
            lastPlayed[name] = now;
            var s = pool[poolNext];
            poolNext = (poolNext + 1) % pool.Length;
            s.Stop();
            s.clip = clip;
            s.pitch = pitch;
            s.volume = Mathf.Clamp01(vol * sfxVolume);
            if (at != null) { s.spatialBlend = 0.85f; s.transform.position = at.Value; }
            else s.spatialBlend = 0;
            s.Play();
        }

        // 手の音。それぞれ 0〜1。stretchPitch: 伸ばす音の高さ (1 = ふつう)
        public static void Hand(int i, Vector3 pos, float rub, float squish, float stretch, float stretchPitch, float brush, float dt)
        {
            if (root == null || voices == null) return;
            var v = voices[i];
            v.t.position = pos;
            float k = 1 - Mathf.Exp(-14 * dt);
            v.vr += (rub - v.vr) * k; v.vs += (squish - v.vs) * k; v.vt += (stretch - v.vt) * k; v.vb += (brush - v.vb) * k;
            v.rub.volume = v.vr * 0.55f * sfxVolume;
            v.rub.pitch = 0.9f + v.vr * 0.25f;
            v.squish.volume = v.vs * 0.8f * sfxVolume;
            v.squish.pitch = 0.85f + v.vs * 0.3f;
            v.stretch.volume = v.vt * 0.35f * sfxVolume;
            v.stretch.pitch = Mathf.Lerp(v.stretch.pitch, Mathf.Clamp(stretchPitch, 0.5f, 2.5f), k);
            v.brush.volume = v.vb * 0.4f * sfxVolume;
        }

        // BGM の雰囲気: calm (作る・待つ) / tense (本番) / none
        public static void Mood(string m) => mood = m;

        public static void Update(float dt)
        {
            if (root == null) return;
            float k = 1 - Mathf.Exp(-1.5f * dt);
            calmK += ((mood == "calm" ? 1 : 0) - calmK) * k;
            tenseK += ((mood == "tense" ? 1 : 0) - tenseK) * k;
            bgmCalm.volume = calmK * bgmVolume * 0.5f;
            bgmTense.volume = tenseK * bgmVolume * 0.6f;
        }

        // ================= 波形を作る =================
        const float TAU = Mathf.PI * 2;

        static void Add(string name, float[] data)
        {
            float peak = 1e-6f;
            foreach (var x in data) peak = Mathf.Max(peak, Mathf.Abs(x));
            if (peak > 0.95f) for (int i = 0; i < data.Length; i++) data[i] *= 0.95f / peak;
            var c = AudioClip.Create(name, data.Length, 1, SR, false);
            c.SetData(data, 0);
            clips[name] = c;
        }

        static float[] Buf(float seconds) => new float[Mathf.Max(1, (int)(seconds * SR))];

        // 鐘のような音 (倍音が速く消える) を buf の at 秒から書き足す。loop: 端を越えたら先頭に回す (BGM)
        static void Bell(float[] buf, float at, float f, float dur, float amp, bool loop = false, float decay = 6)
        {
            int start = (int)(at * SR), n = (int)(dur * SR);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / SR;
                float env = Mathf.Exp(-decay * t) * Mathf.Min(1, t * 400);
                float s = Mathf.Sin(TAU * f * t) + 0.35f * Mathf.Sin(TAU * f * 2.01f * t) * Mathf.Exp(-decay * 2 * t) + 0.15f * Mathf.Sin(TAU * f * 3.02f * t) * Mathf.Exp(-decay * 3 * t);
                int j = start + i;
                if (loop) j %= buf.Length; else if (j >= buf.Length) break;
                buf[j] += s * env * amp;
            }
        }

        // 柔らかいパッド (ゆっくり立ち上がって消える和音の 1 音)
        static void Pad(float[] buf, float at, float f, float dur, float amp)
        {
            int start = (int)(at * SR), n = (int)(dur * SR);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / SR;
                float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / dur));
                env *= env;
                float s = Mathf.Sin(TAU * f * t) + 0.3f * Mathf.Sin(TAU * f * 2 * t + 0.5f * Mathf.Sin(TAU * 0.3f * t));
                buf[(start + i) % buf.Length] += s * env * amp;
            }
        }

        static float Midi(int note) => 440f * Mathf.Pow(2, (note - 69) / 12f);

        // 1 次のローパス
        struct LP { public float y; public float Run(float x, float a) { y += (x - y) * a; return y; } }
        static float Coef(float cutoff) => 1 - Mathf.Exp(-TAU * cutoff / SR);

        // 継ぎ目なくループさせる: 少し長めに作って、最後の部分を先頭に重ねる
        static float[] Seamless(float[] longer, int len)
        {
            int fade = longer.Length - len;
            var o = new float[len];
            for (int i = 0; i < len; i++) o[i] = longer[i];
            for (int i = 0; i < fade && i < len; i++)
            {
                float w = (float)i / fade;
                o[i] = longer[i] * w + longer[len + i] * (1 - w);
            }
            return o;
        }

        static float Noise(System.Random r) => (float)(r.NextDouble() * 2 - 1);

        static void BuildClips(System.Random r)
        {
            // ----- パネル -----
            {
                var b = Buf(0.05f);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    b[i] = (Mathf.Sin(TAU * 1900 * t) * 0.6f + Noise(r) * 0.4f * Mathf.Exp(-t * 400)) * Mathf.Exp(-t * 90);
                }
                Add("click", b);
            }
            {
                var b = Buf(0.03f);
                for (int i = 0; i < b.Length; i++) { float t = (float)i / SR; b[i] = Mathf.Sin(TAU * 1400 * t) * Mathf.Exp(-t * 160) * 0.25f; }
                Add("hover", b);
            }
            {
                var b = Buf(0.02f);
                for (int i = 0; i < b.Length; i++) { float t = (float)i / SR; b[i] = Mathf.Sin(TAU * 3000 * t) * Mathf.Exp(-t * 300) * 0.3f; }
                Add("tick", b);
            }

            // ----- 粘土 -----
            {
                // ぽんっ: 上がっていく音
                var b = Buf(0.18f);
                float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = 260 + 900 * (1 - Mathf.Exp(-t * 30));
                    ph += TAU * f / SR;
                    b[i] = Mathf.Sin(ph) * Mathf.Exp(-t * 22) * Mathf.Min(1, t * 600);
                }
                Add("pop", b);
            }
            {
                // つまむ: 粘土をつかむ「むにっ」(低く下がる丸い音と、こもったノイズを短く)
                var b = Buf(0.16f);
                float ph = 0; var lp = new LP();
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = 170 + 130 * Mathf.Exp(-t * 35);
                    ph += TAU * f / SR;
                    float env = Mathf.Exp(-t * 28) * Mathf.Min(1, t * 400);
                    b[i] = (Mathf.Sin(ph) * 0.8f + lp.Run(Noise(r), Coef(900)) * 1.2f) * env;
                }
                Add("pinch", b);
            }
            {
                // 離す: 伸びた粘土が戻る「ぷにっ」(短く上がる音)
                var b = Buf(0.12f);
                float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = 180 + 220 * (1 - Mathf.Exp(-t * 40));
                    ph += TAU * f / SR;
                    b[i] = Mathf.Sin(ph) * Mathf.Exp(-t * 35) * Mathf.Min(1, t * 500) * 0.7f;
                }
                Add("release", b);
            }
            {
                // どすん: 低い音と、砂ぼこり
                var b = Buf(0.35f);
                float ph = 0; var lp = new LP();
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = 50 + 70 * Mathf.Exp(-t * 18);
                    ph += TAU * f / SR;
                    b[i] = Mathf.Sin(ph) * Mathf.Exp(-t * 12) + lp.Run(Noise(r), Coef(2500)) * Mathf.Exp(-t * 14) * 0.5f;
                }
                Add("land", b);
            }
            {
                // ぼよん: 揺れながら下がる
                var b = Buf(0.6f);
                float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = 170 * (1 + 0.35f * Mathf.Sin(TAU * 11 * t) * Mathf.Exp(-t * 5)) * (1 - 0.25f * t);
                    ph += TAU * f / SR;
                    b[i] = (Mathf.Sin(ph) + 0.3f * Mathf.Sin(ph * 2)) * Mathf.Exp(-t * 5) * Mathf.Min(1, t * 300);
                }
                Add("boing", b);
            }
            {
                // 戻す: 下がる 2 音
                var b = Buf(0.22f);
                Bell(b, 0, 990, 0.12f, 0.5f, false, 25);
                Bell(b, 0.07f, 660, 0.15f, 0.5f, false, 20);
                Add("undo", b);
            }
            {
                // できない: 低いブザー 2 回
                var b = Buf(0.3f);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    bool on = t < 0.11f || (t > 0.15f && t < 0.26f);
                    float sq = Mathf.Sin(TAU * 150 * t) > 0 ? 1 : -1;
                    b[i] = on ? sq * 0.25f : 0;
                }
                var lp = new LP();
                for (int i = 0; i < b.Length; i++) b[i] = lp.Run(b[i], Coef(1200));
                Add("error", b);
            }
            {
                // シュッ (しまう) / シュッ (戻ってくる): 帯域を動かしたノイズ
                foreach (var (name, up) in new[] { ("whoosh", false), ("whooshUp", true) })
                {
                    var b = Buf(0.55f);
                    float low = 0, band = 0;
                    for (int i = 0; i < b.Length; i++)
                    {
                        float t = (float)i / SR, u = t / 0.55f;
                        float fc = up ? Mathf.Lerp(400, 3500, u) : Mathf.Lerp(3500, 300, u);
                        float f = 2 * Mathf.Sin(Mathf.PI * fc / SR);
                        // 状態変数フィルタ (バンドパス)
                        low += f * band;
                        float high = Noise(r) - low - 0.7f * band;
                        band += f * high;
                        b[i] = band * Mathf.Sin(Mathf.PI * u) * 0.8f;
                    }
                    Add(name, b);
                }
            }
            {
                // きらきら (化ける): 高い音を少しずつずらして重ねる
                var b = Buf(1.1f);
                int[] notes = { 84, 88, 91, 96, 100, 103 };
                for (int k = 0; k < 14; k++)
                    Bell(b, k * 0.045f + (float)r.NextDouble() * 0.02f, Midi(notes[k % notes.Length]) * (1 + (float)r.NextDouble() * 0.01f), 0.5f, 0.18f, false, 7);
                Add("shimmer", b);
            }
            {
                // つながった
                var b = Buf(0.8f);
                Bell(b, 0, Midi(88), 0.6f, 0.4f); Bell(b, 0.12f, Midi(91), 0.6f, 0.4f);
                Add("chime", b);
            }

            // ----- ゲームの節目 -----
            {
                // 準備開始: 上がっていく明るい和音
                var b = Buf(1.4f);
                int[] n = { 72, 76, 79, 84 };
                for (int i = 0; i < n.Length; i++) Bell(b, i * 0.11f, Midi(n[i]), 1.0f, 0.35f, false, 3.5f);
                Add("jinglePrepare", b);
            }
            {
                // 本番開始: 短調の 3 音と、低いドン
                var b = Buf(1.4f);
                int[] n = { 57, 60, 64, 69 };
                for (int i = 0; i < n.Length; i++) Bell(b, i * 0.09f, Midi(n[i]), 0.8f, 0.32f, false, 4);
                float ph = 0;
                for (int i = 0; i < (int)(0.6f * SR) && i + (int)(0.36f * SR) < b.Length; i++)
                {
                    float t = (float)i / SR;
                    ph += TAU * (45 + 60 * Mathf.Exp(-t * 20)) / SR;
                    b[i + (int)(0.36f * SR)] += Mathf.Sin(ph) * Mathf.Exp(-t * 6) * 0.8f;
                }
                Add("jinglePlay", b);
            }
            {
                // 終了: 下がっていく 3 音
                var b = Buf(1.6f);
                int[] n = { 79, 76, 72 };
                for (int i = 0; i < n.Length; i++) Bell(b, i * 0.16f, Midi(n[i]), 1.2f, 0.35f, false, 2.5f);
                Add("jingleFinish", b);
            }
            {
                // 残り 10 秒からの秒読み (木の音)、最後の 3 秒は高く
                foreach (var (name, f) in new[] { ("count", 900f), ("count3", 1350f) })
                {
                    var b = Buf(0.08f);
                    for (int i = 0; i < b.Length; i++) { float t = (float)i / SR; b[i] = (Mathf.Sin(TAU * f * t) + 0.4f * Mathf.Sin(TAU * f * 2.7f * t)) * Mathf.Exp(-t * 60) * 0.5f; }
                    Add(name, b);
                }
            }
            {
                // 見つかった: サイレン (高い音と低い音を交互に)
                var b = Buf(1.6f);
                float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = ((int)(t / 0.2f) % 2 == 0) ? 880 : 660;
                    ph += TAU * f / SR;
                    float s = Mathf.Sin(ph) + 0.3f * Mathf.Sin(ph * 3);
                    b[i] = s * 0.35f * Mathf.Min(1, (1.6f - t) * 6);
                }
                Add("alarm", b);
            }

            // ----- 手の音 (ループ) -----
            int L = (int)(2.0f * SR), F = (int)(0.15f * SR);
            {
                // なでる: やわらかい「すりすり」(低めのノイズを少しゆらす)
                var b = new float[L + F]; var lp1 = new LP(); var lp2 = new LP();
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float x = lp2.Run(lp1.Run(Noise(r), Coef(1600)), Coef(1600));
                    b[i] = x * (0.75f + 0.25f * Mathf.Sin(TAU * 5.3f * t)) * 1.8f;
                }
                Add("rubLoop", Seamless(b, L));
            }
            {
                // こねる: 湿った「むにゅ」(低いノイズをゆっくり揺らすだけ。泡のような音は混ぜない)
                var b = new float[L + F]; var lp1 = new LP(); var lp2 = new LP();
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    b[i] = lp2.Run(lp1.Run(Noise(r), Coef(380)), Coef(380)) * 3.6f * (0.75f + 0.25f * Mathf.Sin(TAU * 1.5f * t));
                }
                Add("squishLoop", Seamless(b, L));
            }
            {
                // 伸ばす: やわらかい「みょーん」(丸い音を少し揺らし、こすれるノイズをうっすら混ぜる)。
                // 高さは伸ばした距離で AudioSource.pitch を上げて変える
                var b = new float[L + F]; var lp = new LP(); float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float f = 200 * (1 + 0.015f * Mathf.Sin(TAU * 5 * t));
                    ph += TAU * f / SR;
                    float tone = Mathf.Sin(ph) + 0.2f * Mathf.Sin(ph * 2) + 0.06f * Mathf.Sin(ph * 3);
                    b[i] = tone * 0.45f + lp.Run(Noise(r), Coef(700)) * 0.25f;
                }
                Add("stretchLoop", Seamless(b, L));
            }
            {
                // 塗る: 筆の「さっさっ」(高いノイズ)
                var b = new float[L + F]; var lp = new LP(); float prev = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / SR;
                    float x = Noise(r);
                    float hp = x - prev; prev = x; // 高い方だけ
                    b[i] = lp.Run(hp, Coef(7000)) * (0.7f + 0.3f * Mathf.Sin(TAU * 3.7f * t)) * 0.6f;
                }
                Add("brushLoop", Seamless(b, L));
            }

            // ----- BGM (ループ) -----
            {
                // 作る・待つ: ゆったりした和音とつぶやくような音 (90 BPM、8 小節 ≒ 10.7 秒)
                float beat = 60f / 90f;
                var b = new float[(int)(beat * 16 * SR)];
                int[][] chords = { new[] { 60, 64, 67, 71 }, new[] { 57, 60, 64, 67 }, new[] { 53, 57, 60, 64 }, new[] { 55, 59, 62, 65 } };
                for (int c = 0; c < 4; c++)
                {
                    float at = c * beat * 4;
                    foreach (var n in chords[c]) Pad(b, at, Midi(n), beat * 5f, 0.06f);
                    Pad(b, at, Midi(chords[c][0] - 12), beat * 4.5f, 0.08f);
                    for (int k = 0; k < 8; k++)
                    {
                        int note = chords[c][(k * 3 + c) % 4] + 12;
                        if (k % 2 == 1 && (k + c) % 3 == 0) continue;
                        Bell(b, at + k * beat * 0.5f, Midi(note), 0.9f, 0.05f, true, 4);
                    }
                }
                Add("bgmCalm", b);
            }
            {
                // 本番: 刻むベースと、チクタクいうハイハット (112 BPM、4 小節)
                float beat = 60f / 112f;
                var b = new float[(int)(beat * 16 * SR)];
                int[] roots = { 45, 41, 38, 40 }; // A, F, D, E
                for (int bar = 0; bar < 4; bar++)
                {
                    for (int e = 0; e < 8; e++)
                    {
                        float at = (bar * 4 + e * 0.5f) * beat;
                        int start = (int)(at * SR), n = (int)(beat * 0.45f * SR);
                        float f = Midi(roots[bar] + (e == 6 ? 12 : 0));
                        float ph = 0; var lp = new LP();
                        for (int i = 0; i < n; i++)
                        {
                            float t = (float)i / SR;
                            ph += f / SR; ph -= Mathf.Floor(ph);
                            float saw = ph * 2 - 1;
                            b[(start + i) % b.Length] += lp.Run(saw, Coef(500 + 900 * Mathf.Exp(-t * 20))) * Mathf.Exp(-t * 7) * 0.22f;
                        }
                        // ハイハット (裏拍を強く)
                        int hn = (int)(0.04f * SR); float prev = 0;
                        for (int i = 0; i < hn; i++)
                        {
                            float t = (float)i / SR, x = Noise(r), hp = x - prev; prev = x;
                            b[(start + i) % b.Length] += hp * Mathf.Exp(-t * 120) * (e % 2 == 1 ? 0.09f : 0.04f);
                        }
                    }
                    // 小節の頭に低いドン
                    {
                        int start = (int)(bar * 4 * beat * SR); float ph = 0;
                        for (int i = 0; i < (int)(0.3f * SR); i++)
                        {
                            float t = (float)i / SR;
                            ph += TAU * (48 + 50 * Mathf.Exp(-t * 25)) / SR;
                            b[(start + i) % b.Length] += Mathf.Sin(ph) * Mathf.Exp(-t * 9) * 0.35f;
                        }
                    }
                    // 上で鳴る不安げな音 (2 拍目と 4 拍目の裏)
                    int[] hi = { 76, 77, 74, 75 };
                    Bell(b, (bar * 4 + 1.5f) * beat, Midi(hi[bar]), 0.6f, 0.05f, true, 5);
                    Bell(b, (bar * 4 + 3.5f) * beat, Midi(hi[bar] - 5), 0.6f, 0.04f, true, 5);
                }
                Add("bgmTense", b);
            }
        }
    }
}
