// ローカルキャッシュ。
//
// レイアウト:
//   <root>/index.json          署名対象のバイト列そのもの
//   <root>/index.json.sig      サイドカー署名 (raw 64 bytes)
//   <root>/chunks/<sha256 の小文字 hex 64 桁>.json   ← **内容アドレス** (フィード相対パスではない)
//   <root>/meta.json           最終取得時刻 (ISO-8601 UTC)
//
// **チャンクはフィード相対パスではなく内容の hash で置く**。これが torn write に対する
// 耐久性の要点: 改訂されていないチャンクは世代が変わっても同じファイルなので、新しい世代の
// 書き込みが前の世代のファイルを上書きしない。書き込み途中で落ちても「新しいファイルが
// 増えただけ」になり、まだ差し替わっていない古い index はそのまま全チャンクを満たす =
// **前回キャッシュが生き残る**。
//
// 旧レイアウト (chunks/items/00.json) では逆で、改訂チャンクを書いた直後に落ちると
// 「新しい本文 + 古い index」が残り、次のロードが不一致を汚染とみなしてキャッシュ**全体**を
// 捨てていた (実測)。ディスク満杯や AV ロックで書き込みが途中で失敗した場合も同じだった。
//
// **ファイル入出力はバイト列だけで行う**。文字列で読み書きする API を使うと、環境や
// 設定次第で改行や BOM が混ざる余地ができる。2026-08-13 に .gitattributes の穴で
// Windows の checkout が LF→CRLF 変換を掛け、署名対象が 1 バイトも合わなくなって
// C# の conformance テストが windows-latest でだけ落ちた — 同じ罠がキャッシュの
// 読み書きにもある (こちらは CI に出ず、利用者の環境でだけ壊れる)。
//
// **TryLoad はロードのたびに署名とチャンク hash を再検証する**。これが設計の要点で、
// 「書き込み途中で落ちたキャッシュが信頼される経路」を構造的に消す — 原子的書き込みの
// 正しさに賭けるのではなく、読む側が毎回確かめるので、汚染は必ず検出される。
//
// **キャッシュ全体を捨てるのは index を信用できない時だけ** (署名検証に失敗した / index が
// 自分の不変条件を満たしていない / そもそも読めない)。チャンク側の異常は 1 枚単位で諦める:
//   - index にあってローカルに無い          → 欠落 (取れていない 1 枚。縮退)
//   - あるが hash が一致しない (壊れている)  → 欠落として扱い、その 1 枚を消す
// 後者は旧レイアウトでは「汚染」だった。名前が内容の hash になった今、その名前のファイルは
// 「一致する」か「壊れている」かのどちらかしかなく、**前の世代の別の本文がそこに残ることは
// 構造的に起こらない** — 事故で読めない 1 枚のために前回キャッシュを全損させる理由が消えた。
//
// 移行: 旧レイアウトのキャッシュを持つ利用者は全チャンクが「欠落」になり、次の取得で埋まる
// (v0.1 未出荷なので移行コードは置かない。旧ファイルは PruneChunks が片付ける)。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mizprism.LicenseLens
{
    /// <summary>取得の記録 (meta.json)。署名対象ではないので、壊れていても安全側に倒す。</summary>
    public sealed class FeedCacheMeta
    {
        internal static readonly FeedCacheMeta Empty = new FeedCacheMeta(null, null);

        internal FeedCacheMeta(DateTimeOffset? lastSuccessUtc, DateTimeOffset? lastAttemptUtc)
        {
            LastSuccessUtc = lastSuccessUtc;
            LastAttemptUtc = lastAttemptUtc;
        }

        /// <summary>最後に**成功した**取得の時刻 (1 日 1 回の上限はこちらで数える)。</summary>
        public DateTimeOffset? LastSuccessUtc { get; }

        /// <summary>最後に**ネットワークに触れた**時刻 (成否を問わない。連打を止める門で使う)。</summary>
        public DateTimeOffset? LastAttemptUtc { get; }
    }

    /// <summary>再検証を通ったキャッシュの内容。</summary>
    public sealed class CachedFeed
    {
        private readonly Dictionary<string, byte[]> _chunks;
        private readonly List<string> _missingChunks;

        internal CachedFeed(FeedIndex index, Dictionary<string, byte[]> chunks,
                            List<string> missingChunks, FeedCacheMeta meta)
        {
            Index = index;
            _chunks = chunks;
            _missingChunks = missingChunks;
            Meta = meta;
        }

        public FeedIndex Index { get; }

        /// <summary>最終「成功」取得時刻と最終試行時刻。</summary>
        public FeedCacheMeta Meta { get; }

        /// <summary>最後に取得が成功した時刻。</summary>
        public DateTimeOffset? LastSuccessUtc => Meta.LastSuccessUtc;

        /// <summary>index に載っているのにローカルに無いチャンク (取得に失敗した分)。</summary>
        public IReadOnlyList<string> MissingChunks => _missingChunks;

        /// <summary>検証済み index が持つ署名 (別に持ち回らない — 対応しない組を作らせないため)。</summary>
        public byte[] Signature => Index.Signature;

        /// <summary>チャンク本体 (ロード時に index と照合済み)。複製を返す。</summary>
        public bool TryGetChunk(string path, out byte[] body)
        {
            body = null;
            byte[] stored;
            if (path == null || !_chunks.TryGetValue(path, out stored)) return false;
            body = new byte[stored.Length];
            Buffer.BlockCopy(stored, 0, body, 0, stored.Length);
            return true;
        }

        internal bool TryGetChunkReference(string path, out byte[] body)
        {
            body = null;
            return path != null && _chunks.TryGetValue(path, out body);
        }
    }

    public sealed class FeedCache
    {
        private const string IndexFileName = "index.json";
        private const string SignatureFileName = "index.json.sig";
        private const string MetaFileName = "meta.json";
        private const string ChunksDirName = "chunks";
        private const string ChunkFileSuffix = ".json";
        private const string TempSuffix = ".tmp";
        private const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

        // index の sha256 の表記 (FeedIndex.TryParse がここに合わせて検査している)。
        private const string Sha256Prefix = "sha256:";
        private const int Sha256HexLength = 64;

        private readonly string _root;

        public FeedCache(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
                throw new ArgumentException("キャッシュのルートディレクトリが空", nameof(rootDirectory));
            _root = rootDirectory;
        }

        public string Root => _root;

        /// <summary>
        /// キャッシュを読み、署名とチャンク hash を**再検証**する。
        /// false になるのは **index を信用できない時だけ** (署名検証の失敗 / index の不変条件違反 /
        /// そもそも読めない)。チャンク側の異常 (欠落・破損) は 1 枚単位で諦めて true を返す。
        /// </summary>
        public bool TryLoad(FeedKeyring keyring, out CachedFeed cached, out string reason)
        {
            cached = null;
            reason = null;
            try
            {
                string indexPath = Path.Combine(_root, IndexFileName);
                string signaturePath = Path.Combine(_root, SignatureFileName);
                if (!File.Exists(indexPath) || !File.Exists(signaturePath))
                {
                    reason = "キャッシュが無い (" + _root + ")";
                    return false;
                }

                byte[] indexBytes = File.ReadAllBytes(indexPath);
                byte[] signature = File.ReadAllBytes(signaturePath);

                FeedIndex index;
                FeedVerifyResult verdict = FeedVerifier.VerifyIndex(indexBytes, signature, keyring, out index);
                if (!verdict.IsOk)
                {
                    // キャッシュは自分で書いたものだが、書いた後に何が起きたかは判らない
                    // (torn write / 外からの書き換え / 鍵の失効)。信じずに毎回確かめる。
                    reason = "キャッシュの index を再検証できない: " + verdict.Reason;
                    return false;
                }

                var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var missing = new List<string>();
                for (int i = 0; i < index.Chunks.Count; i++)
                {
                    FeedChunkEntry entry = index.Chunks[i];
                    string path = ChunkFilePath(entry.Sha256);
                    if (path == null)
                    {
                        // 署名を通った index が、自分の不変条件 (sha256 の表記) を満たしていない。
                        // チャンク 1 枚の事故ではなく index を信用できない状態なので、ここは
                        // 全体を捨てる側に倒す (FeedIndex の検査が効いている限り到達しない)。
                        reason = "キャッシュのチャンクパスを組み立てられない: " + entry.Path;
                        return false;
                    }
                    if (!File.Exists(path))
                    {
                        missing.Add(entry.Path); // 取れていない 1 枚。汚染ではないので続行する
                        continue;
                    }

                    byte[] body = File.ReadAllBytes(path);
                    string why;
                    if (!FeedVerifier.VerifyChunk(index, entry.Path, body, out why))
                    {
                        // 名前が内容の hash なので、この 1 枚は「一致する」か「壊れている」かの
                        // どちらかしかない (前の世代の別の本文がここに残ることは無い)。壊れているのは
                        // 汚染ではなく事故なので、キャッシュ全体ではなくこの 1 枚だけを諦める。
                        missing.Add(entry.Path);
                        TryDeleteFile(path); // 消せれば次の取得で埋め直せる (消せなくても毎回ここで弾く)
                        continue;
                    }
                    chunks[entry.Path] = body;
                }

                cached = new CachedFeed(index, chunks, missing, ReadMeta());
                return true;
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                reason = "キャッシュを読めない: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 検証済み index と、その index に一致するチャンク本体を書き込む。
        /// 署名は index が持っているものを使う (呼び出し側に別の署名を渡させない)。
        ///
        /// チャンクは内容アドレスで置くので、chunkBodies に無い entry でも、その本文が既に
        /// 手元にあれば (= 前の世代で取得済みで今回改訂されていなければ) そのまま新しい index を
        /// 満たす。したがって**空の辞書は「全消し」ではなく「index だけ差し替え」**を意味する。
        /// null は誤用として弾く (何を指示されたのか判らないまま書き込みを始めない)。
        /// </summary>
        public bool TryStore(FeedIndex index, IDictionary<string, byte[]> chunkBodies,
                             DateTimeOffset fetchedAtUtc, out string error)
        {
            error = null;
            if (index == null) { error = "index が無い"; return false; }
            if (chunkBodies == null)
            {
                error = "チャンクが渡されていない (空の辞書と null を区別する: null は誤用、" +
                        "空は index だけを差し替える指示)";
                return false;
            }

            try
            {
                Directory.CreateDirectory(_root);
                if (!TryWriteChunks(index, chunkBodies, out error)) return false;

                // index を最後に書く。ここまでで落ちた場合に残るのは「新しいチャンクが増えただけ」の
                // 状態で、古い index はその全チャンクを引き続き満たす (内容アドレスなので、新しい
                // 本文が古い本文を上書きしない) = **前回キャッシュが生き残る**。
                //
                // 残る窓は署名と index の 2 ファイルの間だけ (片方だけ新しくなると署名検証が落ち、
                // キャッシュは取り直しになる)。1 ファイルに畳まない限り消えない窓なので、ここでは
                // 幅を最小にする以上のことはしない。
                WriteBytesAtomically(Path.Combine(_root, SignatureFileName), index.SignatureReference);
                WriteBytesAtomically(Path.Combine(_root, IndexFileName), index.RawBytesReference);
                WriteMeta(fetchedAtUtc, fetchedAtUtc);

                // prune は index を commit した**後**に走る。前の世代のファイルが消えるのは
                // 新しい index が確定してからなので、上の窓では何も失われない。
                PruneChunks(ReferencedFileNames(index));
                return true;
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                error = "キャッシュに書けない: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 手元の index はそのままに、**チャンクの本文だけ**を書き足す。
        ///
        /// index / 署名 / meta を書き換えず、prune もしない。取り損ねた 1 枚を後から埋め直す修復
        /// (FeedClient の修復パス) のためのもので、「新しい世代を commit した」ことにはならない —
        /// 最終取得時刻を動かすかどうかの判断は呼び出し側に残す。
        /// </summary>
        public bool TryStoreChunks(FeedIndex index, IDictionary<string, byte[]> chunkBodies, out string error)
        {
            error = null;
            if (index == null) { error = "index が無い"; return false; }
            if (chunkBodies == null) { error = "チャンクが渡されていない"; return false; }

            try
            {
                Directory.CreateDirectory(_root);
                return TryWriteChunks(index, chunkBodies, out error);
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                error = "キャッシュに書けない: " + e.Message;
                return false;
            }
        }

        /// <summary>チャンク本体を照合してから書く (index / meta には触れない)。</summary>
        private bool TryWriteChunks(FeedIndex index, IDictionary<string, byte[]> chunkBodies, out string error)
        {
            error = null;

            // 書く前に全部照合する。index と食い違うものをキャッシュに置かない
            // (置いた瞬間、そのチャンクは次回の TryLoad で欠落扱いになる = 黙って消える)。
            foreach (KeyValuePair<string, byte[]> pair in chunkBodies)
            {
                string why;
                if (!FeedVerifier.VerifyChunk(index, pair.Key, pair.Value, out why))
                {
                    error = "検証を通らないチャンクは書かない: " + why;
                    return false;
                }
            }

            foreach (KeyValuePair<string, byte[]> pair in chunkBodies)
            {
                // 上の照合を通った = index に載っている entry なので、hash はここで必ず引ける。
                FeedChunkEntry entry;
                string path = index.TryGetChunk(pair.Key, out entry) ? ChunkFilePath(entry.Sha256) : null;
                if (path == null)
                {
                    error = "チャンクのファイル名を決められない: " + pair.Key;
                    return false;
                }
                WriteChunkFile(path, pair.Value);
            }
            return true;
        }

        /// <summary>
        /// 取得の記録を読む。読めない・無い場合は空 (= 取得してよい) を返す。
        ///
        /// **成功と試行を分けて持つ**のが要点: 上限 1 日 1 回は「成功した取得」
        /// に掛ける一方、失敗した取得も**ネットワークに触れている**以上、連打を許してよい
        /// 理由にはならない (運用条件「低レートで取りに行く」)。1 つの時刻で両方は表せない。
        /// </summary>
        public FeedCacheMeta ReadMeta()
        {
            try
            {
                string path = Path.Combine(_root, MetaFileName);
                if (!File.Exists(path)) return FeedCacheMeta.Empty;

                JsonValue root;
                string parseError;
                if (!Json.TryParse(File.ReadAllBytes(path), out root, out parseError)) return FeedCacheMeta.Empty;
                if (root.Kind != JsonKind.Object) return FeedCacheMeta.Empty;

                return new FeedCacheMeta(ReadTime(root, "last_success_utc"), ReadTime(root, "last_attempt_utc"));
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                // meta は署名対象ではなく、失っても安全側 (= 取得してよい) に倒れるだけなので、
                // 読めないことをキャッシュ全体の失敗にしない。
                return FeedCacheMeta.Empty;
            }
        }

        /// <summary>
        /// 「取りに行った」ことだけを記録する (成功時刻は据え置き)。
        /// **ネットワークに触れる前**に呼ぶ — 失敗した取得が記録に残らないと、連打の門が効かない。
        /// </summary>
        public bool TryRecordAttempt(DateTimeOffset attemptUtc, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(_root);
                WriteMeta(ReadMeta().LastSuccessUtc, attemptUtc);
                return true;
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                error = "取得試行を記録できない: " + e.Message;
                return false;
            }
        }

        /// <summary>ISO-8601 UTC 表記に直す (meta.json とログで同じ書式を使う)。</summary>
        public static string FormatUtc(DateTimeOffset value) =>
            value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

        private static DateTimeOffset? ReadTime(JsonValue root, string name)
        {
            string text;
            if (!root.TryGetString(name, out text)) return null;

            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                return null;
            }
            return parsed;
        }

        private void WriteMeta(DateTimeOffset? successUtc, DateTimeOffset? attemptUtc)
        {
            // フィードの正規化バイト列に倣った書式 (キー順固定 / indent=1 / 末尾改行)。meta は
            // 署名対象ではないので厳密である必要はないが、フィード側と見た目を揃えておくと読みやすい。
            var sb = new StringBuilder("{\n");
            if (attemptUtc.HasValue)
                sb.Append(" \"last_attempt_utc\": \"").Append(FormatUtc(attemptUtc.Value)).Append("\"");
            if (attemptUtc.HasValue && successUtc.HasValue) sb.Append(",\n");
            if (successUtc.HasValue)
                sb.Append(" \"last_success_utc\": \"").Append(FormatUtc(successUtc.Value)).Append("\"");
            sb.Append("\n}\n");

            WriteBytesAtomically(Path.Combine(_root, MetaFileName),
                                 new UTF8Encoding(false).GetBytes(sb.ToString()));
        }

        /// <summary>
        /// commit した index が参照しないファイルを片付ける。
        ///
        /// 判定の基準は「今回書いたか」ではなく「**いま commit した index が参照する内容か**」。
        /// 内容アドレスでは、改訂されていないチャンクは今回書かなくてもそのまま新しい index を
        /// 満たすので、旧レイアウト時代の「index にあるのに今回書かなかったものも消す」という
        /// 規律 (古い本文が新しい index の hash と食い違ってキャッシュ全体を道連れにするのを
        /// 防ぐためのもの) は、名前が hash になった時点で不要になった — むしろ消すと、手元に
        /// ある正しい本文を捨てることになる。
        ///
        /// ここで消えるのは前の世代の残骸・書き残しの .tmp・旧レイアウトの chunks/items/… だけ。
        /// </summary>
        private void PruneChunks(HashSet<string> referencedFileNames)
        {
            string chunksRoot = Path.Combine(_root, ChunksDirName);
            if (!Directory.Exists(chunksRoot)) return;

            string[] files = Directory.GetFiles(chunksRoot, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                // 相対パスで比べる (サブディレクトリに同名のファイルがあっても、それは読まれる
                // ファイルではない = 残骸なので消す)。
                string relative = files[i].Substring(chunksRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (referencedFileNames.Contains(relative)) continue;
                TryDeleteFile(files[i]); // 消すのは自分が作ったキャッシュ配下だけ
            }
        }

        /// <summary>この index が参照するチャンクファイル名の集合。</summary>
        private static HashSet<string> ReferencedFileNames(FeedIndex index)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < index.Chunks.Count; i++)
            {
                string name = ChunkFileName(index.Chunks[i].Sha256);
                if (name != null) names.Add(name);
            }
            return names;
        }

        /// <summary>index の sha256 からキャッシュ上のファイルパスを作る。作れなければ null。</summary>
        private string ChunkFilePath(string sha256)
        {
            string name = ChunkFileName(sha256);
            return name == null ? null : Path.Combine(_root, ChunksDirName, name);
        }

        /// <summary>"sha256:&lt;小文字 hex 64 桁&gt;" → "&lt;hex&gt;.json"。それ以外は null。</summary>
        private static string ChunkFileName(string sha256)
        {
            // ここに来る前に FeedIndex が表記を検査済みだが、二重に確かめる。ファイル名が決まるのは
            // ここなので、「1 ヶ所でも検査を抜けたら終わり」の性質は旧レイアウト (フィード相対パスを
            // そのままファイルパスにしていた頃のパストラバーサル) と変わらない。
            //
            // **この行を消してもテストは緑のまま**になる (FeedIndex 側の検査が先に落とすので、
            // public API からは到達できない)。ミューテーション試験で生き残るのは想定どおりで、
            // 「テストが無いから不要」ではない — FeedIndex の検査が将来緩んだ時の最後の砦。
            if (sha256 == null || !sha256.StartsWith(Sha256Prefix, StringComparison.Ordinal)) return null;

            string hex = sha256.Substring(Sha256Prefix.Length);
            if (!FeedHex.IsLowerHex(hex, Sha256HexLength)) return null; // 区切り文字も大文字も入り得ない
            return hex + ChunkFileSuffix;
        }

        /// <summary>
        /// チャンクを 1 枚書く。**同じ名前のファイルが既にあれば触らない**。
        ///
        /// 名前が内容の hash なので、既にあるファイルは同じ内容 (でなければ壊れている)。上書きは
        /// delete → move の順で行われる = ほんの一瞬「そのチャンクがディスクに無い」状態を作るので、
        /// 要らない上書きはしない方が耐久性が高い。壊れていた場合は TryLoad が欠落として扱って
        /// 消すので、次の取得で書き直される。
        /// </summary>
        private static void WriteChunkFile(string path, byte[] body)
        {
            if (File.Exists(path)) return;
            WriteBytesAtomically(path, body);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                // 消せなくても安全側: そのファイルは TryLoad で毎回弾かれる (欠落として扱われる)。
            }
        }

        private static void WriteBytesAtomically(string path, byte[] content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // netstandard2.1 に 3 引数の File.Move (上書き) は無いので、delete → move で置換する。
            string temp = path + TempSuffix;
            File.WriteAllBytes(temp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static bool IsFileSystemFailure(Exception e) =>
            e is IOException ||
            e is UnauthorizedAccessException ||
            e is System.Security.SecurityException ||
            e is ArgumentException ||
            e is NotSupportedException;
    }
}
