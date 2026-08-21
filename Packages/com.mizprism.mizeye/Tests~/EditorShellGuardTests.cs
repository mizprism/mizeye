// 構造ガード: Editor/ に判断を置かせない。
//
// Editor アセンブリは UnityEditor に依存するので、CI の dotnet ジョブでは **1 行も
// コンパイルされない**。テストも netstandard ビルドも掛からないため、ここに置かれた
// 判断は誰にも検査されないまま利用者へ届く。
//
// 表示層の設計は「判断は Runtime/View、描画は Editor」だが、**設計は放っておくと破れる**
// — 描画中に `if (value == "allowed")` を 1 行書くのは自然な動作で、レビューでも見落とす。
// 破れても他のテストは緑のままなので、ここでしか赤くならない。
//
// 見張るのは「語彙とスキーマのキーが Editor に現れないこと」。表示層でズレが起きるのは
// ほぼ必ずこの形 (Runtime/View の語彙を直さずに Editor 側で条件分岐を足す) で、
// そうなると 2 つの実装が別々に腐る (収集側で実際に踏んだのと同型)。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class EditorShellGuardTests
    {
        // スキーマの値と、レコードのキー。どれも意味の解釈が要る語で、解釈は
        // LicenseVocabulary / TermsDetailView にしか置かない。
        private static readonly string[] BannedTokens =
        {
            "\"allowed\"", "\"conditional\"", "\"forbidden\"", "\"unclear\"",
            "\"not_required\"", "\"required\"",
            "attributes_derived", "permission_matrix", "matrix_overrides",
            "item_conditions", "terms_ref", "content_hash",
            // 生データを Editor で解釈し始めた兆候
            "Json.TryParse", "FeedTerms.TryParse", "FeedItem.TryParse"
        };

        private static string EditorSourceDir() =>
            FeedFixtures.PackageDir("Editor");

        private static Dictionary<string, string> EditorSources()
        {
            string dir = EditorSourceDir();
            Assert.True(Directory.Exists(dir), "Editor/ が無い: " + dir);

            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            foreach (string file in files)
                sources[Path.GetFileName(file)] = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
            return sources;
        }

        /// <summary>
        /// 行コメントは対象外。**禁止しているのは「解釈すること」であって「言及すること」ではない**
        /// — 理由を書けなくすると、次に読む人が同じ判断を再発明する
        /// (FeedStructureGuardTests が同じ理由で同じ扱いにしている)。
        /// </summary>
        private static IEnumerable<string> NonCommentLines(string source)
        {
            string[] lines = source.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                yield return raw;
            }
        }

        [Fact]
        public void TheEditorShellDoesNotInterpretSchemaValues()
        {
            Dictionary<string, string> sources = EditorSources();
            Assert.NotEmpty(sources); // 0 件を「違反なし」と読まない

            var violations = new List<string>();
            foreach (KeyValuePair<string, string> source in sources)
            {
                foreach (string line in NonCommentLines(source.Value))
                {
                    foreach (string token in BannedTokens)
                    {
                        if (line.IndexOf(token, StringComparison.Ordinal) >= 0)
                            violations.Add(source.Key + " が " + token + " を解釈している: " + line.Trim());
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "判断は Runtime/View に置く (Editor は CI でコンパイルされない = 検証されない):\n  " +
                string.Join("\n  ", violations.ToArray()));
        }

        [Fact]
        public void TheGuardCanActuallySeeSourceText()
        {
            // 上のガードが「読めていないから緑」になっていないことの確認。
            // 実在するはずの語を探して、見つからなければガード自体が壊れている。
            Dictionary<string, string> sources = EditorSources();
            string window;
            Assert.True(sources.TryGetValue("LicenseLensWindow.cs", out window),
                "LicenseLensWindow.cs を読めていない — ガードは何も見ていない");
            Assert.Contains("EditorWindow", window, StringComparison.Ordinal);
            Assert.Contains("AssetListView", window, StringComparison.Ordinal);
        }

        [Fact]
        public void TheEditorShellDoesNotPersistIntoTheProject()
        {
            // 「プロジェクトを一切変更しない」。Assets/ に書き込む経路が入ると、
            // 読むだけのはずのツールがプロジェクトを変更する。EditorPrefs (プロジェクト外) を使う。
            string[] banned =
            {
                "AssetDatabase.CreateAsset", "AssetDatabase.SaveAssets", "AssetDatabase.ImportAsset",
                "File.WriteAllBytes", "File.WriteAllText", "File.Create", "Directory.CreateDirectory",
                "PrefabUtility.Save"
            };

            var violations = new List<string>();
            foreach (KeyValuePair<string, string> source in EditorSources())
            {
                foreach (string line in NonCommentLines(source.Value))
                {
                    foreach (string token in banned)
                    {
                        if (line.IndexOf(token, StringComparison.Ordinal) >= 0)
                            violations.Add(source.Key + ": " + token);
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "パッケージはプロジェクトを変更しない:\n  " + string.Join("\n  ", violations.ToArray()));
        }
    }
}
