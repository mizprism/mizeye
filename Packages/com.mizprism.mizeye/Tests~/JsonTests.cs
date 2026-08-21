// 最小 JSON パーサの受け入れテスト。
//
// 測っているのは「読めること」ではなく **「読まないこと」** — 重複キー・末尾のゴミ・
// 不正 UTF-8・単独サロゲート・深すぎる入れ子を通さないこと。このパーサは署名検証の
// **前**に signing_key_id を読む位置にいる (FeedVerifier の順序 (1)) ので、寛容さは
// そのまま攻撃面になる。

using System;
using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class JsonTests
    {
        private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

        private static JsonValue ParseOk(string json)
        {
            JsonValue value;
            string error;
            Assert.True(Json.TryParse(Utf8(json), out value, out error), "拒否されるべきでない入力を拒否した: " + error);
            return value;
        }

        private static string ParseError(string json)
        {
            JsonValue value;
            string error;
            Assert.False(Json.TryParse(Utf8(json), out value, out error), "通してはいけない入力を通した: " + json);
            Assert.Null(value);
            Assert.NotNull(error);
            return error;
        }

        [Fact]
        public void ParsesNestedDocument()
        {
            JsonValue root = ParseOk(
                "{\n \"chunks\": [{\"path\": \"items/00.json\", \"bytes\": 317, \"deep\": {\"a\": [1, 2, {\"b\": null}]}}],\n" +
                " \"name\": \"テスト素体 あお\",\n \"flag\": true,\n \"ratio\": -1.5e3\n}");

            Assert.Equal(JsonKind.Object, root.Kind);

            JsonValue chunks;
            Assert.True(root.TryGetArray("chunks", out chunks));
            Assert.Single(chunks.Items);

            string path;
            Assert.True(chunks.Items[0].TryGetString("path", out path));
            Assert.Equal("items/00.json", path);

            long bytes;
            Assert.True(chunks.Items[0].TryGetInt64("bytes", out bytes));
            Assert.Equal(317L, bytes);

            JsonValue deep;
            Assert.True(chunks.Items[0].TryGetObject("deep", out deep));
            JsonValue a;
            Assert.True(deep.TryGetArray("a", out a));
            Assert.Equal(3, a.Items.Count);
            Assert.True(a.Items[2].TryGetMember("b", out JsonValue b));
            Assert.True(b.IsNull);

            // 非 ASCII はそのまま読めること (フィードは ensure_ascii=False で書かれる)
            string name;
            Assert.True(root.TryGetString("name", out name));
            Assert.Equal("テスト素体 あお", name);

            JsonValue flag;
            Assert.True(root.TryGetMember("flag", out flag));
            Assert.True(flag.BoolValue);

            // 小数・指数は「数値」としては受けるが、整数としては読ませない
            JsonValue ratio;
            Assert.True(root.TryGetMember("ratio", out ratio));
            Assert.Equal(JsonKind.Number, ratio.Kind);
            long asInt;
            Assert.False(ratio.TryGetInt64(out asInt));
        }

        [Fact]
        public void RejectsDuplicateKeys()
        {
            // 署名済みフィードに重複キーがあると、「同じ署名で意味が 2 通り」になる。
            string error = ParseError("{\"signing_key_id\": \"sha256:aa\", \"signing_key_id\": \"sha256:bb\"}");
            Assert.Contains("重複", error);
        }

        [Fact]
        public void RejectsTrailingGarbage()
        {
            string error = ParseError("{\"a\": 1} {\"b\": 2}");
            Assert.Contains("余分な文字", error);
            ParseError("{\"a\": 1}\n\0");
        }

        [Fact]
        public void RejectsInvalidUtf8()
        {
            // 0xFF は UTF-8 に現れないバイト。置換文字にして黙って通すと、検証済みのつもりで
            // 元と違うものを読むことになる。
            byte[] invalid = new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"',
                                          0xFF, (byte)'"', (byte)'}' };
            JsonValue value;
            string error;
            Assert.False(Json.TryParse(invalid, out value, out error));
            Assert.Contains("UTF-8", error);
        }

        [Fact]
        public void HandlesSurrogatePairs()
        {
            JsonValue root = ParseOk("{\"emoji\": \"\\uD83D\\uDC4D\", \"kana\": \"\\u3042\"}");
            string emoji, kana;
            Assert.True(root.TryGetString("emoji", out emoji));
            Assert.True(root.TryGetString("kana", out kana));
            Assert.Equal("👍", emoji);
            Assert.Equal("あ", kana);

            // 片割れだけのサロゲートは通さない (string として不正な並びを作らせない)
            Assert.Contains("サロゲート", ParseError("{\"broken\": \"\\uD83D\"}"));
            Assert.Contains("サロゲート", ParseError("{\"broken\": \"\\uDC4D\"}"));
            Assert.Contains("サロゲート", ParseError("{\"broken\": \"\\uD83D\\u0041\"}"));
        }

        [Fact]
        public void RejectsTooDeepNesting()
        {
            Assert.Equal(64, Json.MaxDepth);
            ParseOk(Nested(Json.MaxDepth));                              // 上限ちょうどは通る
            Assert.Contains("深すぎる", ParseError(Nested(Json.MaxDepth + 1)));

            // 実際に敵対的な深さ (再帰でスタックを溢れさせにくる形) でも例外が漏れないこと
            Assert.Contains("深すぎる", ParseError(Nested(5000)));
        }

        private static string Nested(int depth)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < depth; i++) sb.Append('[');
            for (int i = 0; i < depth; i++) sb.Append(']');
            return sb.ToString();
        }

        [Fact]
        public void RejectsRawControlCharactersInStrings()
        {
            Assert.Contains("制御文字", ParseError("{\"a\": \"line\nbreak\"}"));
            Assert.Contains("制御文字", ParseError("{\"a\": \"tab\there\"}"));
        }

        [Fact]
        public void RejectsSloppyNumbersAndLiterals()
        {
            ParseError("{\"a\": 01}");      // 先頭 0
            ParseError("{\"a\": +1}");      // 先頭 +
            ParseError("{\"a\": .5}");
            ParseError("{\"a\": 1.}");
            ParseError("{\"a\": NaN}");
            ParseError("{\"a\": Infinity}");
            ParseError("{\"a\": TRUE}");
            ParseError("{\"a\": 1,}");      // 末尾カンマ
            ParseError("{\"a\": 1");        // 閉じていない
            ParseError("{a: 1}");           // 引用符の無いキー
            ParseError("");
        }

        [Fact]
        public void RejectsByteOrderMark()
        {
            // BOM 付きで保存された index は署名対象のバイト列としても別物になる。
            byte[] withBom = new UTF8Encoding(true).GetPreamble();
            byte[] body = Utf8("{\"a\": 1}");
            var combined = new byte[withBom.Length + body.Length];
            Buffer.BlockCopy(withBom, 0, combined, 0, withBom.Length);
            Buffer.BlockCopy(body, 0, combined, withBom.Length, body.Length);

            JsonValue value;
            string error;
            Assert.False(Json.TryParse(combined, out value, out error));
            Assert.Contains("BOM", error);
        }
    }
}
