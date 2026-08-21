// 構造ガード: 実装の「形」そのものを測る。
//
// 中身の振る舞いは他のテストが測るが、次の 2 つは**後から静かに壊せる**ので形で守る:
//
//  1. **バイト規律** — Runtime/Feed/ 配下で文字列ベースのファイル入出力を使わない。
//     2026-08-13、.gitattributes の穴で Windows の checkout が LF→CRLF 変換を掛け、
//     署名対象のバイト列が変わって conformance テストが windows-latest でだけ落ちた。
//     同じ罠がキャッシュの読み書きにあり、そちらは CI に出ず利用者の環境でだけ壊れる。
//     「今は使っていない」は 1 行の追加で消える性質なので、機械で見張る。
//
//  2. **未検証データを作れないこと** — FeedIndex に public なコンストラクタや
//     public な TryParse が生えたら、「署名検証を通った証拠」という型の意味が消える。
//     消えても他のテストは緑のままなので、ここでしか赤くならない。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedStructureGuardTests
    {
        // 文字列としてファイルを読み書きする経路は**全部**塞ぐ。1 つでも残っていると、
        // 「ここだけテキストで読む」が入り込み、そこだけ改行変換の影響を受ける。
        // (AppendText は AppendAllText の部分文字列ではないので、両方を並べる必要がある。)
        private static readonly string[] BannedTokens =
        {
            "ReadAllText", "WriteAllText", "AppendAllText", "AppendText",
            "ReadAllLines", "WriteAllLines", "AppendAllLines",
            "StreamReader", "StreamWriter", "OpenText", "CreateText", "ReadLines",
            "Encoding.Default", "Encoding.ASCII"
        };

        private static string FeedSourceDir() =>
            FeedFixtures.PackageDir("Runtime", "Feed");

        private static Dictionary<string, string> FeedSources()
        {
            string dir = FeedSourceDir();
            Assert.True(Directory.Exists(dir), "Runtime/Feed が無い: " + dir);

            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            foreach (string file in files)
                sources[Path.GetFileName(file)] = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
            return sources;
        }

        [Fact]
        public void FeedSourcesDoNotUseTextFileApis()
        {
            Dictionary<string, string> sources = FeedSources();
            Assert.NotEmpty(sources); // 0 件を「違反なし」と読まない (空の結果は根拠にならない)

            var violations = new List<string>();
            foreach (KeyValuePair<string, string> source in sources)
            {
                foreach (string token in BannedTokens)
                {
                    if (source.Value.IndexOf(token, StringComparison.Ordinal) >= 0)
                        violations.Add(source.Key + " が " + token + " を使っている");
                }
            }

            Assert.True(violations.Count == 0,
                "Runtime/Feed ではバイト列でのみ入出力する (改行変換で署名対象が壊れる):\n  " +
                string.Join("\n  ", violations.ToArray()));
        }

        [Fact]
        public void TheGuardCanActuallySeeSourceText()
        {
            // 上のガードが「読めていないから緑」になっていないことの確認。実際に使っている
            // バイト列 API が検出できるなら、禁止トークンも検出できる。
            Dictionary<string, string> sources = FeedSources();
            string cache;
            Assert.True(sources.TryGetValue("FeedCache.cs", out cache));
            Assert.Contains("File.ReadAllBytes", cache);
            Assert.Contains("File.WriteAllBytes", cache);

            // 検出器そのものの動作確認 (同じ判定式で、含む文字列を含むと判定できるか)
            const string sample = "var x = File.ReadAllText(path);";
            bool detected = false;
            foreach (string token in BannedTokens)
                if (sample.IndexOf(token, StringComparison.Ordinal) >= 0) detected = true;
            Assert.True(detected, "検出器が禁止トークンを見つけられない");
        }

        [Fact]
        public void VerifiedTypesCannotBeConstructedByCallers()
        {
            // FeedIndex は「署名検証を通ったバイト列」の証拠。外から作れてはいけない。
            Assert.Empty(typeof(FeedIndex).GetConstructors());
            Assert.Null(typeof(FeedIndex).GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static));

            Assert.Empty(typeof(FeedChunkEntry).GetConstructors());
            Assert.Empty(typeof(CachedFeed).GetConstructors());
            Assert.Empty(typeof(FeedItem).GetConstructors());
            Assert.Empty(typeof(FeedKey).GetConstructors());
            Assert.Empty(typeof(FeedKeyring).GetConstructors()); // 生成は TryParse 経由のみ

            // terms 側も同じ規律。ここに足さないと、型が増えるたびに「検証済みチャンクからしか
            // 作らない」が実装者の記憶頼みになる。
            Assert.Empty(typeof(FeedTerms).GetConstructors());
            Assert.Null(typeof(FeedTerms).GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static));
            Assert.Empty(typeof(FeedTermsCell).GetConstructors());
            Assert.Empty(typeof(FeedTermsOverride).GetConstructors());
            Assert.Empty(typeof(FeedTermsCondition).GetConstructors());
            Assert.Empty(typeof(FeedTermsSource).GetConstructors());
            Assert.Empty(typeof(FeedTermsHistoryEntry).GetConstructors());
        }

        [Fact]
        public void SignaturesDoNotTravelOutsideTheVerifiedIndex()
        {
            // 「検証済み index」と「その署名」が別々の裸の byte[] で持ち回られると、対応しない
            // 組を作れる。実測: v1 の index + v2 の署名でキャッシュが書け、以後 TryLoad が毎回
            // 失敗して回復不能になった (しかも meta は書かれるので取り直しも止まっていた)。
            // 型として構成不能にしたことを、API の形で固定する。
            Assert.NotNull(typeof(FeedIndex).GetProperty("Signature"));

            // TryStore だけを名指しすると、書き込み口が増えた時に見張りが追いつかない
            // (実際 TryStoreChunks が増えた)。**書き込み口を全部**見る。
            var writers = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(FeedCache).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (method.Name.StartsWith("TryStore", StringComparison.Ordinal)) writers.Add(method);
            Assert.NotEmpty(writers); // 0 件を「違反なし」と読まない

            foreach (MethodInfo writer in writers)
            {
                foreach (ParameterInfo parameter in writer.GetParameters())
                {
                    Assert.False(parameter.ParameterType == typeof(byte[]),
                        writer.Name + " が裸の byte[] (" + parameter.Name + ") を受けている");
                }
            }
        }

        [Fact]
        public void SafeValuesSitAtZero()
        {
            // 既定値 (0) は常に「安全な側」でなければならない。初期化し忘れた値が
            // 「検証済み」「到達できた」「有効な鍵」として扱われる経路を作らない。
            Assert.Equal(0, (int)FeedVerdict.Unverified);
            Assert.Equal(0, (int)FeedKeyStatus.Unknown);
            Assert.False(default(FeedVerifyResult).IsOk);
            Assert.False(default(FeedTransportResult).Reachable);
        }

        [Fact]
        public void FeedSourcesDoNotReferenceUnityEngineOrJsonPackages()
        {
            // Runtime asmdef は noEngineReferences: true / references: []。エンジン参照や
            // 外部 JSON ライブラリが混ざると、Unity 内でのビルドとこのテストプロジェクトの
            // どちらかでしか通らなくなる (= 検証が効かない側で出荷しうる)。
            //
            // 行コメントは対象外にする。ここでの禁止は「依存すること」であって「言及すること」
            // ではない — 理由を書けなくすると、次に読む人が同じ判断を再発明する。
            string[] banned = { "UnityEngine", "UnityEditor", "Newtonsoft", "System.Text.Json" };

            var violations = new List<string>();
            foreach (KeyValuePair<string, string> source in FeedSources())
            {
                foreach (string line in NonCommentLines(source.Value))
                {
                    foreach (string token in banned)
                        if (line.IndexOf(token, StringComparison.Ordinal) >= 0)
                            violations.Add(source.Key + ": " + line.Trim());
                }
            }
            Assert.True(violations.Count == 0, string.Join("\n  ", violations.ToArray()));

            // 検出器の力の確認 (コメント除外を入れた結果、何も見なくなっていないこと)
            var sample = new List<string>(NonCommentLines("// using UnityEngine;\nusing UnityEngine;\n"));
            Assert.Single(sample);
            Assert.Contains("UnityEngine", sample[0]);
        }

        private static IEnumerable<string> NonCommentLines(string source)
        {
            string[] lines = source.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                yield return line;
            }
        }
    }
}
