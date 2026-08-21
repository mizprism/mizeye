// 最小・厳格な JSON パーサ (署名付きフィードを読むためだけのもの)。
//
// なぜ自前なのか: Unity 2022.3 (netstandard2.1) の標準に System.Text.Json は無く、
// JsonUtility はエンジン参照を要求する (Runtime asmdef は noEngineReferences: true)。
// Newtonsoft を足すのは「依存は VPM resolver のみ」に反する。
//
// なぜ「厳格」なのか: このパーサは署名検証を**通す前**の index からも signing_key_id を
// 読む (FeedVerifier の順序 (1))。つまり敵対的入力が入り得る場所にいる。曖昧な入力を
// 「それらしく」解釈する寛容なパーサは、書いた側と読んだ側で意味が割れる余地を作る —
// 重複キーがその代表で、どちらを採るかで結果が変わるのに署名は通ってしまう。
// 迷ったら落とす (fail closed)。
//
// 公開範囲は internal。フィードの意味を持つ型 (FeedIndex / FeedKeyring) だけを外に出し、
// 「生の JSON を自分でパースして使う」経路を呼び出し側に与えない。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mizprism.LicenseLens
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>JSON の解析失敗。データ層の public API はこれを外に漏らさず verdict に変換する。</summary>
    internal sealed class JsonParseException : Exception
    {
        internal JsonParseException(string message) : base(message)
        {
        }
    }

    internal sealed class JsonValue
    {
        private static readonly IReadOnlyList<JsonValue> NoItems = new JsonValue[0];
        private static readonly KeyValuePair<string, JsonValue>[] NoMembers = new KeyValuePair<string, JsonValue>[0];

        private readonly JsonKind _kind;
        private readonly bool _bool;
        private readonly string _text;                            // String の値 / Number の字句
        private readonly List<JsonValue> _items;                  // Array
        private readonly Dictionary<string, JsonValue> _members;  // Object

        private JsonValue(JsonKind kind, bool b, string text,
                          List<JsonValue> items, Dictionary<string, JsonValue> members)
        {
            _kind = kind;
            _bool = b;
            _text = text;
            _items = items;
            _members = members;
        }

        internal static readonly JsonValue Null = new JsonValue(JsonKind.Null, false, null, null, null);
        internal static readonly JsonValue True = new JsonValue(JsonKind.Bool, true, null, null, null);
        internal static readonly JsonValue False = new JsonValue(JsonKind.Bool, false, null, null, null);

        internal static JsonValue FromString(string s) =>
            new JsonValue(JsonKind.String, false, s, null, null);

        internal static JsonValue FromNumber(string lexeme) =>
            new JsonValue(JsonKind.Number, false, lexeme, null, null);

        internal static JsonValue FromArray(List<JsonValue> items) =>
            new JsonValue(JsonKind.Array, false, null, items, null);

        internal static JsonValue FromObject(Dictionary<string, JsonValue> members) =>
            new JsonValue(JsonKind.Object, false, null, null, members);

        internal JsonKind Kind => _kind;
        internal bool IsNull => _kind == JsonKind.Null;

        /// <summary>String の値。Number では字句そのもの (数値化は TryGetInt64 が行う)。</summary>
        internal string Text => _text;

        internal bool BoolValue => _bool;

        internal IReadOnlyList<JsonValue> Items =>
            _kind == JsonKind.Array ? (IReadOnlyList<JsonValue>)_items : NoItems;

        internal int Count =>
            _kind == JsonKind.Array ? _items.Count : (_kind == JsonKind.Object ? _members.Count : 0);

        /// <summary>
        /// Object のメンバ列挙 (Object 以外では空)。**キー集合が仕様で閉じていない物**
        /// (permission_matrix の 24 キーは方言追加で増えうる) を読むために要る — 読む側に
        /// キー名を焼き込むと、フィードが増やした行が黙って落ちる。
        ///
        /// 列挙順は保証しない (下敷きは Dictionary)。順序が要る呼び出し側は自分で並べ替えること。
        /// </summary>
        internal IEnumerable<KeyValuePair<string, JsonValue>> Members =>
            _kind == JsonKind.Object ? _members : NoMembers;

        internal bool TryGetMember(string name, out JsonValue value)
        {
            value = null;
            return _kind == JsonKind.Object && _members.TryGetValue(name, out value);
        }

        internal bool TryGetString(string name, out string value)
        {
            value = null;
            JsonValue v;
            if (!TryGetMember(name, out v) || v.Kind != JsonKind.String) return false;
            value = v.Text;
            return true;
        }

        internal bool TryGetArray(string name, out JsonValue value)
        {
            value = null;
            JsonValue v;
            if (!TryGetMember(name, out v) || v.Kind != JsonKind.Array) return false;
            value = v;
            return true;
        }

        internal bool TryGetObject(string name, out JsonValue value)
        {
            value = null;
            JsonValue v;
            if (!TryGetMember(name, out v) || v.Kind != JsonKind.Object) return false;
            value = v;
            return true;
        }

        /// <summary>整数として読む。小数・指数表記は拒否する (id や件数に化けさせない)。</summary>
        internal bool TryGetInt64(out long value)
        {
            value = 0;
            if (_kind != JsonKind.Number) return false;
            return long.TryParse(_text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        internal bool TryGetInt64(string name, out long value)
        {
            value = 0;
            JsonValue v;
            return TryGetMember(name, out v) && v.TryGetInt64(out value);
        }
    }

    internal static class Json
    {
        /// <summary>入れ子の上限。敵対的な深い配列でスタックを溢れさせないための門。</summary>
        internal const int MaxDepth = 64;

        /// <summary>UTF-8 として解釈し、解析する。失敗は JsonParseException。</summary>
        internal static JsonValue Parse(byte[] utf8)
        {
            if (utf8 == null) throw new JsonParseException("入力が null");

            string text;
            try
            {
                // throwOnInvalidBytes: true — 不正な UTF-8 を U+FFFD に置換して黙って通さない。
                // 置換すると「元のバイト列とは違うもの」を読んだまま検証済みのつもりになる。
                text = new UTF8Encoding(false, true).GetString(utf8);
            }
            catch (DecoderFallbackException e)
            {
                throw new JsonParseException("UTF-8 として不正なバイト列: " + e.Message);
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                // RFC 8259 は BOM を認めない。フィードの正本バイト列 (正規化された形)
                // にも BOM は無いので、あれば別物。
                throw new JsonParseException("先頭に BOM がある (JSON テキストとして不正)");
            }

            var parser = new Parser(text);
            return parser.ParseDocument();
        }

        internal static bool TryParse(byte[] utf8, out JsonValue value, out string error)
        {
            value = null;
            error = null;
            try
            {
                value = Parse(utf8);
                return true;
            }
            catch (JsonParseException e)
            {
                error = e.Message;
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private int _depth;

            internal Parser(string s)
            {
                _s = s;
                _i = 0;
                _depth = 0;
            }

            internal JsonValue ParseDocument()
            {
                SkipWhitespace();
                JsonValue v = ParseValue();
                SkipWhitespace();
                if (_i != _s.Length)
                {
                    // 末尾のゴミを黙って捨てない。署名対象のバイト列に「読まれない部分」が
                    // あってはいけない (読み手ごとに解釈が割れる余地になる)。
                    throw Err("トップレベル値の後ろに余分な文字がある");
                }
                return v;
            }

            private JsonValue ParseValue()
            {
                if (_i >= _s.Length) throw Err("値が来る位置で入力が終わった");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return JsonValue.FromString(ParseString());
                    case 't': ExpectLiteral("true"); return JsonValue.True;
                    case 'f': ExpectLiteral("false"); return JsonValue.False;
                    case 'n': ExpectLiteral("null"); return JsonValue.Null;
                    default: return ParseNumber();
                }
            }

            private JsonValue ParseObject()
            {
                EnterDepth();
                _i++; // '{'
                var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal); // 序数比較 (文化圏で同一視させない)
                SkipWhitespace();
                if (Peek() == '}') { _i++; _depth--; return JsonValue.FromObject(members); }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Err("オブジェクトのキーは文字列でなければならない");
                    string key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':') throw Err("キーの後に ':' が無い");
                    _i++;
                    SkipWhitespace();
                    JsonValue value = ParseValue();

                    if (members.ContainsKey(key))
                    {
                        // 署名済みフィードに重複キーがあるのは危険信号。どちらを採るかで
                        // 意味が変わるのに署名は通るため、「同じ署名で別の内容」を作れてしまう。
                        throw Err("キーが重複している: " + key);
                    }
                    members.Add(key, value);

                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { _i++; continue; }
                    if (c == '}') { _i++; break; }
                    throw Err("オブジェクトの区切りが ',' でも '}' でもない");
                }
                _depth--;
                return JsonValue.FromObject(members);
            }

            private JsonValue ParseArray()
            {
                EnterDepth();
                _i++; // '['
                var items = new List<JsonValue>();
                SkipWhitespace();
                if (Peek() == ']') { _i++; _depth--; return JsonValue.FromArray(items); }

                while (true)
                {
                    SkipWhitespace();
                    items.Add(ParseValue());
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { _i++; continue; }
                    if (c == ']') { _i++; break; }
                    throw Err("配列の区切りが ',' でも ']' でもない");
                }
                _depth--;
                return JsonValue.FromArray(items);
            }

            private string ParseString()
            {
                _i++; // '"'
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw Err("文字列が閉じていない");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        sb.Append(ParseEscape());
                        continue;
                    }
                    if (c < 0x20)
                    {
                        // 生の制御文字は RFC 8259 で禁止。通すと、目に見えない差分を含む
                        // 2 つの「同じに見える」文字列が作れる。
                        throw Err("文字列中に生の制御文字がある (U+" +
                                  ((int)c).ToString("X4", CultureInfo.InvariantCulture) + ")");
                    }
                    sb.Append(c);
                }
                return sb.ToString();
            }

            private string ParseEscape()
            {
                if (_i >= _s.Length) throw Err("エスケープが途中で終わっている");
                char c = _s[_i++];
                switch (c)
                {
                    case '"': return "\"";
                    case '\\': return "\\";
                    case '/': return "/";
                    case 'b': return "\b";
                    case 'f': return "\f";
                    case 'n': return "\n";
                    case 'r': return "\r";
                    case 't': return "\t";
                    case 'u': break;
                    default: throw Err("未知のエスケープ \\" + c);
                }

                int hi = ParseHex4();
                if (hi >= 0xD800 && hi <= 0xDBFF)
                {
                    // 上位サロゲートは必ず下位サロゲートと対で来る。片割れだけを通すと
                    // .NET の string としては不正な並びが混ざり、比較や再符号化で壊れる。
                    if (_i + 1 >= _s.Length || _s[_i] != '\\' || _s[_i + 1] != 'u')
                        throw Err("上位サロゲートの後に下位サロゲートが無い");
                    _i += 2;
                    int lo = ParseHex4();
                    if (lo < 0xDC00 || lo > 0xDFFF) throw Err("下位サロゲートの範囲外");
                    return new string(new[] { (char)hi, (char)lo });
                }
                if (hi >= 0xDC00 && hi <= 0xDFFF) throw Err("下位サロゲートが単独で現れた");
                return new string((char)hi, 1);
            }

            private int ParseHex4()
            {
                if (_i + 4 > _s.Length) throw Err("\\u の後の 16 進 4 桁が足りない");
                int v = 0;
                for (int k = 0; k < 4; k++)
                {
                    char c = _s[_i++];
                    int d;
                    if (c >= '0' && c <= '9') d = c - '0';
                    else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                    else throw Err("\\u の後が 16 進数でない: " + c);
                    v = (v << 4) | d;
                }
                return v;
            }

            /// <summary>RFC 8259 の数値文法だけを受ける (先頭 0・先頭 +・NaN/Infinity は拒否)。</summary>
            private JsonValue ParseNumber()
            {
                int start = _i;
                if (Peek() == '-') _i++;

                if (_i >= _s.Length) throw Err("数値が途中で終わっている");
                if (_s[_i] == '0')
                {
                    _i++;
                }
                else if (_s[_i] >= '1' && _s[_i] <= '9')
                {
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                else
                {
                    throw Err("値として解釈できない文字: " + _s[_i]);
                }

                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    int digits = 0;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') { _i++; digits++; }
                    if (digits == 0) throw Err("小数点の後に数字が無い");
                }

                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    int digits = 0;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') { _i++; digits++; }
                    if (digits == 0) throw Err("指数部に数字が無い");
                }

                return JsonValue.FromNumber(_s.Substring(start, _i - start));
            }

            private void ExpectLiteral(string literal)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw Err("リテラル " + literal + " として読めない");
                }
                _i += literal.Length;
            }

            private char Peek() => _i < _s.Length ? _s[_i] : '\0';

            private void SkipWhitespace()
            {
                // RFC 8259 の空白は 4 種類だけ。全角空白等を空白扱いしない。
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            private void EnterDepth()
            {
                _depth++;
                if (_depth > MaxDepth)
                    throw Err("入れ子が深すぎる (上限 " + MaxDepth + ")");
            }

            private JsonParseException Err(string message) =>
                new JsonParseException(message + " (位置 " + _i.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }
}
