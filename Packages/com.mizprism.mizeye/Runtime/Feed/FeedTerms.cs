// terms チャンク 1 本分のレコード (規約そのもの)。
//
// **検証済みチャンクからしか作らない** (internal TryParse を FeedClient だけが呼ぶ)。
// FeedItem と同じ規律: 未検証のパース済みデータを呼び出し側に持たせない。
//
// パースの対象は**シードのレコードではなく配信形** (フィード生成器が組む形) —
// シードレコード全体に `permission_matrix_effective` を足した形。したがってここは
// **2 つの層を別々に**持つ (v1.2 の層分離):
//
//   - `permission_matrix` = **行字義** (原文の各行をそのまま構造化した値 + 逐語引用)
//   - `matrix_overrides`  = **横断適用** (特記事項などが複数行を覆う構造化レコード)
//   - `permission_matrix_effective.values` = 上 2 つの**決定論合成の結果 (導出)**
//
// **この 3 つを 1 つの辞書に畳まない**。畳むと「原文にそう書いてある」と「合成でそうなる」の
// 区別が消え、引用に辿れない値を原文の顔で見せることになる。
//
// フィールドは v0.1 のレコード実物 (全 51 件で実測) に合わせた集合で、
// **未知のフィールドは黙って無視する** (record_schema_versions が上がった時に古いパッケージが
// 「読めない」で全滅しないため)。逆に terms_id / permission_matrix が欠けたレコードは、
// 同定にも表示にも使えないので拒否する。
//
// **欠けている値に既定値を発明しない**。任意フィールドは null / 空コレクションのまま返す —
// 「規約に書いていない」を「許可」や「不明」に化けさせない。判定値 (value / to) も enum に
// 潰さず**生の文字列**で持つ: 実測で allowed / forbidden / conditional / unclear の 4 値の他に
// not_required / required / note が出ており、方言が増えれば更に出る。知らない値を unclear に
// 丸めると、規約の言っていないことを言うことになる。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    /// <summary>permission_matrix の 1 セル (行字義)。</summary>
    public sealed class FeedTermsCell
    {
        internal FeedTermsCell(string value, string note, string quote)
        {
            Value = value;
            Note = note;
            Quote = quote;
        }

        /// <summary>
        /// 判定値の**生の文字列** (allowed / conditional / forbidden / unclear / not_required /
        /// required / note …)。未知の値も潰さずそのまま返す。
        /// </summary>
        public string Value { get; }

        /// <summary>構造化した側の補足 (無ければ null)。</summary>
        public string Note { get; }

        /// <summary>原文からの逐語引用 (無ければ null)。**各規約の権利者に帰属する** (LICENSING.md)。</summary>
        public string Quote { get; }
    }

    /// <summary>特記事項などが複数行を横断して覆う適用 (行字義には畳まれていない)。</summary>
    public sealed class FeedTermsOverride
    {
        private readonly List<string> _keys;

        internal FeedTermsOverride(List<string> keys, string to, string quote, string note, string basis)
        {
            _keys = keys;
            To = to;
            Quote = quote;
            Note = note;
            Basis = basis;
        }

        /// <summary>覆われる permission_matrix のキー。</summary>
        public IReadOnlyList<string> Keys => _keys;

        /// <summary>覆った後の判定値 (生の文字列。<see cref="FeedTermsCell.Value"/> と同じ語彙)。</summary>
        public string To { get; }

        /// <summary>原文からの逐語引用。</summary>
        public string Quote { get; }
        public string Note { get; }

        /// <summary>どこを根拠に覆っているか (実測値: special_notes)。</summary>
        public string Basis { get; }
    }

    /// <summary>
    /// terms 側の個別条項。<see cref="FeedItemCondition"/> と同じ形だが**別の層**のもの —
    /// あちらは「この商品だけの条項」、こちらは「この規約の条項」。混ぜると、共有 terms の
    /// 注意書きが特定商品の条件として出る。
    /// </summary>
    public sealed class FeedTermsCondition
    {
        internal FeedTermsCondition(string matrixKey, string note, string quote)
        {
            MatrixKey = matrixKey;
            Note = note;
            Quote = quote;
        }

        /// <summary>permission_matrix のキー (フィールド名は実レコードが正 = matrix_key)。</summary>
        public string MatrixKey { get; }
        public string Note { get; }

        /// <summary>原文からの逐語引用。**各規約の権利者に帰属する**。</summary>
        public string Quote { get; }
    }

    /// <summary>規約原文の在処 (原文へ案内するための出典)。</summary>
    public sealed class FeedTermsSource
    {
        internal FeedTermsSource(string url, string locationType, string role, string fetchedAt,
                                 string contentHash, long? pages, string note)
        {
            Url = url;
            LocationType = locationType;
            Role = role;
            FetchedAt = fetchedAt;
            ContentHash = contentHash;
            Pages = pages;
            Note = note;
        }

        public string Url { get; }

        /// <summary>原文の置かれ方 (実測値: booth_description / external_google_drive_pdf / …)。</summary>
        public string LocationType { get; }

        /// <summary>正本か補助か (実測値: primary / secondary / entry)。</summary>
        public string Role { get; }
        public string FetchedAt { get; }

        /// <summary>取得時点の原文の hash ("sha256:…")。改訂検知の根拠。</summary>
        public string ContentHash { get; }

        /// <summary>PDF のページ数 (該当しなければ null)。</summary>
        public long? Pages { get; }
        public string Note { get; }
    }

    /// <summary>規約の改訂履歴 1 件。</summary>
    public sealed class FeedTermsHistoryEntry
    {
        internal FeedTermsHistoryEntry(string changedAt, string summary)
        {
            ChangedAt = changedAt;
            Summary = summary;
        }

        public string ChangedAt { get; }
        public string Summary { get; }
    }

    public sealed class FeedTerms
    {
        private readonly Dictionary<string, FeedTermsCell> _permissionMatrix;
        private readonly List<FeedTermsOverride> _overrides;
        private readonly Dictionary<string, string> _effective;
        private readonly Dictionary<string, string> _attributes;
        private readonly List<FeedTermsCondition> _conditions;
        private readonly List<FeedTermsSource> _sources;
        private readonly List<string> _unclearPoints;
        private readonly List<FeedTermsHistoryEntry> _history;

        internal FeedTerms(string termsId, string title, string rightsHolder, string dialect,
                           string termsVersion, string schemaVersion, string creditDisplay,
                           string recommendedHashtag, string recommendedHashtagNote, string scopeNote,
                           string structuringNotes, string confidence, string confidenceBasis,
                           string lastVerifiedAt, string effectiveDerivation, string verification,
                           Dictionary<string, FeedTermsCell> permissionMatrix,
                           List<FeedTermsOverride> overrides,
                           Dictionary<string, string> effective,
                           Dictionary<string, string> attributes,
                           List<FeedTermsCondition> conditions,
                           List<FeedTermsSource> sources,
                           List<string> unclearPoints,
                           List<FeedTermsHistoryEntry> history)
        {
            TermsId = termsId;
            Title = title;
            RightsHolder = rightsHolder;
            Dialect = dialect;
            TermsVersion = termsVersion;
            SchemaVersion = schemaVersion;
            CreditDisplay = creditDisplay;
            RecommendedHashtag = recommendedHashtag;
            RecommendedHashtagNote = recommendedHashtagNote;
            ScopeNote = scopeNote;
            StructuringNotes = structuringNotes;
            Confidence = confidence;
            ConfidenceBasis = confidenceBasis;
            LastVerifiedAt = lastVerifiedAt;
            EffectiveDerivation = effectiveDerivation;
            Verification = verification;
            _permissionMatrix = permissionMatrix;
            _overrides = overrides;
            _effective = effective;
            _attributes = attributes;
            _conditions = conditions;
            _sources = sources;
            _unclearPoints = unclearPoints;
            _history = history;
        }

        /// <summary>この規約の同定子 ("sha256:…" または "visual:…" — v1.3)。item の terms_ref と突き合わせる値。</summary>
        public string TermsId { get; }

        public string Title { get; }
        public string RightsHolder { get; }

        /// <summary>規約の方言 (実測値: vn3-template 39 件 / custom 12 件、2026-08-14 の 51 レコード)。</summary>
        public string Dialect { get; }

        /// <summary>規約自身が名乗る版 (無ければ null)。</summary>
        public string TermsVersion { get; }

        /// <summary>レコードのスキーマ版 ("1.2" 等)。</summary>
        public string SchemaVersion { get; }

        /// <summary>権利者が示すクレジット表記 (無ければ null)。</summary>
        public string CreditDisplay { get; }

        public string RecommendedHashtag { get; }
        public string RecommendedHashtagNote { get; }

        /// <summary>この規約が及ぶ範囲についての注記 (無ければ null)。</summary>
        public string ScopeNote { get; }

        /// <summary>構造化した側の作業メモ (無ければ null)。</summary>
        public string StructuringNotes { get; }

        /// <summary>構造化の確信度 (実測値: high / medium / low)。</summary>
        public string Confidence { get; }

        /// <summary>その確信度の根拠。</summary>
        public string ConfidenceBasis { get; }

        /// <summary>この規約を最後に原文と突き合わせた時刻 (ISO 8601 UTC)。</summary>
        public string LastVerifiedAt { get; }

        /// <summary>
        /// 行字義の層。**原文にそう書いてある値**であり、横断適用 (<see cref="MatrixOverrides"/>) は
        /// 反映されていない。キーは実レコードのものをそのまま持つ (A_individual_use … X_special_notes、
        /// 実測 24 キー) — 消費側でキー集合を決め打ちすると、方言が行を増やした時に黙って落ちる。
        /// 並び順が要るなら序数比較で並べ替えること (キーの接頭辞がその順序になっている)。
        /// </summary>
        public IReadOnlyDictionary<string, FeedTermsCell> PermissionMatrix => _permissionMatrix;

        /// <summary>特記事項などによる横断適用 (行字義には畳まれていない)。無ければ空。</summary>
        public IReadOnlyList<FeedTermsOverride> MatrixOverrides => _overrides;

        /// <summary>
        /// **実効値の層 (導出)**。permission_matrix と matrix_overrides の決定論合成であり、
        /// 保存レイヤーではない (正本は行字義 + 横断適用の 2 層)。原文照合には
        /// <see cref="PermissionMatrix"/> の quote を使うこと — こちらには引用が無い。
        ///
        /// 配信形以外 (シードのレコード等) を読むと**空**になる。空を「全キーが不明」と読まない。
        /// </summary>
        public IReadOnlyDictionary<string, string> PermissionMatrixEffective => _effective;

        /// <summary>実効値がどう導かれたかについてのフィード自身の記述 (permission_matrix_effective._derived)。</summary>
        public string EffectiveDerivation { get; }

        /// <summary>
        /// 検証様式 (schema v1.3、配信形のみ): "bytes" = snapshot 済みで hash 検証可能、
        /// "visual" = 所有者制限で snapshot が無く hash 検証不能 (視覚読解由来 — 変化検知は再訪目視のみ)。
        /// シードレコードには無い (フィードを生成する側が足す) — null を「bytes」と読まないこと。
        /// UI は visual を利用者に可視化する義務がある (検証様式の差を黙って均さない)。
        /// </summary>
        public string Verification { get; }

        /// <summary>
        /// 用途に近い粒度へまとめた属性 (実測 7 キー: commercial_use / avatar_modification /
        /// redistribution / parts_reuse / credit_required / monetized_streaming /
        /// commissioned_editing)。**これも導出**であり、根拠は行字義 + 横断適用の側にある。
        /// キーを決め打ちしないのは permission_matrix と同じ理由。
        /// </summary>
        public IReadOnlyDictionary<string, string> AttributesDerived => _attributes;

        /// <summary>この規約の個別条項 (matrix の 1 行に収まらない条件)。無ければ空。</summary>
        public IReadOnlyList<FeedTermsCondition> Conditions => _conditions;

        /// <summary>規約原文の在処。**原文への案内はここから作る**。</summary>
        public IReadOnlyList<FeedTermsSource> Sources => _sources;

        /// <summary>構造化しきれなかった論点 (そのまま利用者に見せてよい)。無ければ空。</summary>
        public IReadOnlyList<string> UnclearPoints => _unclearPoints;

        /// <summary>規約の改訂履歴。無ければ空。</summary>
        public IReadOnlyList<FeedTermsHistoryEntry> History => _history;

        internal static bool TryParse(JsonValue obj, out FeedTerms terms, out string error)
        {
            terms = null;
            error = null;
            if (obj == null || obj.Kind != JsonKind.Object)
            {
                error = "terms がオブジェクトでない";
                return false;
            }

            string termsId;
            if (!obj.TryGetString("terms_id", out termsId) || termsId.Length == 0)
            {
                error = "terms に terms_id が無い (item の terms_ref と突き合わせられない)";
                return false;
            }

            JsonValue matrixObject;
            if (!obj.TryGetObject("permission_matrix", out matrixObject))
            {
                error = "terms に permission_matrix が無い (" + termsId + ")";
                return false;
            }

            var matrix = new Dictionary<string, FeedTermsCell>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonValue> member in matrixObject.Members)
            {
                if (member.Value.Kind != JsonKind.Object)
                {
                    error = "permission_matrix[" + member.Key + "] がオブジェクトでない";
                    return false;
                }
                string value, note, quote;
                if (!member.Value.TryGetString("value", out value))
                {
                    // 判定値の無いセルは表示できない。空欄で出すと「規約に書いていない」と
                    // 「読み取れなかった」が同じ顔になるので、レコードごと拒否する。
                    error = "permission_matrix[" + member.Key + "] に value が無い";
                    return false;
                }
                member.Value.TryGetString("note", out note);
                member.Value.TryGetString("quote", out quote);
                matrix[member.Key] = new FeedTermsCell(value, note, quote);
            }

            var overrides = new List<FeedTermsOverride>();
            JsonValue overridesArray;
            if (obj.TryGetArray("matrix_overrides", out overridesArray))
            {
                for (int i = 0; i < overridesArray.Items.Count; i++)
                {
                    JsonValue o = overridesArray.Items[i];
                    if (o.Kind != JsonKind.Object)
                    {
                        error = "matrix_overrides[" + i + "] がオブジェクトでない";
                        return false;
                    }
                    var keys = new List<string>();
                    JsonValue keysArray;
                    if (o.TryGetArray("keys", out keysArray))
                    {
                        for (int k = 0; k < keysArray.Items.Count; k++)
                        {
                            JsonValue key = keysArray.Items[k];
                            if (key.Kind != JsonKind.String)
                            {
                                error = "matrix_overrides[" + i + "].keys[" + k + "] が文字列でない";
                                return false;
                            }
                            keys.Add(key.Text);
                        }
                    }
                    string to, quote, note, basis;
                    o.TryGetString("to", out to);
                    o.TryGetString("quote", out quote);
                    o.TryGetString("note", out note);
                    o.TryGetString("basis", out basis);
                    overrides.Add(new FeedTermsOverride(keys, to, quote, note, basis));
                }
            }

            // 実効値は配信形にしか無い (フィードを生成する側が合成して足す)。無ければ空のまま —
            // ここで合成し直さない。合成をクライアントに実装すると、フィードと消費側で
            // 実効値の定義が 2 つになる。
            var effective = new Dictionary<string, string>(StringComparer.Ordinal);
            string effectiveDerivation = null;
            JsonValue effectiveObject;
            if (obj.TryGetObject("permission_matrix_effective", out effectiveObject))
            {
                effectiveObject.TryGetString("_derived", out effectiveDerivation);
                JsonValue values;
                if (effectiveObject.TryGetObject("values", out values))
                {
                    foreach (KeyValuePair<string, JsonValue> member in values.Members)
                    {
                        if (member.Value.Kind != JsonKind.String)
                        {
                            error = "permission_matrix_effective.values[" + member.Key + "] が文字列でない";
                            return false;
                        }
                        effective[member.Key] = member.Value.Text;
                    }
                }
            }

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonValue attributesObject;
            if (obj.TryGetObject("attributes_derived", out attributesObject))
            {
                foreach (KeyValuePair<string, JsonValue> member in attributesObject.Members)
                {
                    if (member.Value.Kind != JsonKind.String)
                    {
                        error = "attributes_derived[" + member.Key + "] が文字列でない";
                        return false;
                    }
                    attributes[member.Key] = member.Value.Text;
                }
            }

            var conditions = new List<FeedTermsCondition>();
            JsonValue conditionsArray;
            if (obj.TryGetArray("conditions", out conditionsArray))
            {
                for (int i = 0; i < conditionsArray.Items.Count; i++)
                {
                    JsonValue c = conditionsArray.Items[i];
                    if (c.Kind != JsonKind.Object)
                    {
                        error = "conditions[" + i + "] がオブジェクトでない";
                        return false;
                    }
                    string matrixKey, note, quote;
                    c.TryGetString("matrix_key", out matrixKey);
                    c.TryGetString("note", out note);
                    c.TryGetString("quote", out quote);
                    conditions.Add(new FeedTermsCondition(matrixKey, note, quote));
                }
            }

            var sources = new List<FeedTermsSource>();
            JsonValue sourcesArray;
            if (obj.TryGetArray("sources", out sourcesArray))
            {
                for (int i = 0; i < sourcesArray.Items.Count; i++)
                {
                    JsonValue s = sourcesArray.Items[i];
                    if (s.Kind != JsonKind.Object)
                    {
                        error = "sources[" + i + "] がオブジェクトでない";
                        return false;
                    }
                    string url, locationType, role, fetchedAt, contentHash, note;
                    s.TryGetString("url", out url);
                    s.TryGetString("location_type", out locationType);
                    s.TryGetString("role", out role);
                    s.TryGetString("fetched_at", out fetchedAt);
                    s.TryGetString("content_hash", out contentHash);
                    s.TryGetString("note", out note);

                    long pageCount;
                    long? pages = s.TryGetInt64("pages", out pageCount) ? (long?)pageCount : null;

                    sources.Add(new FeedTermsSource(url, locationType, role, fetchedAt, contentHash, pages, note));
                }
            }

            var unclearPoints = new List<string>();
            JsonValue unclearArray;
            if (obj.TryGetArray("unclear_points", out unclearArray))
            {
                for (int i = 0; i < unclearArray.Items.Count; i++)
                {
                    JsonValue u = unclearArray.Items[i];
                    if (u.Kind != JsonKind.String)
                    {
                        error = "unclear_points[" + i + "] が文字列でない";
                        return false;
                    }
                    unclearPoints.Add(u.Text);
                }
            }

            var history = new List<FeedTermsHistoryEntry>();
            JsonValue historyArray;
            if (obj.TryGetArray("history", out historyArray))
            {
                for (int i = 0; i < historyArray.Items.Count; i++)
                {
                    JsonValue h = historyArray.Items[i];
                    if (h.Kind != JsonKind.Object)
                    {
                        error = "history[" + i + "] がオブジェクトでない";
                        return false;
                    }
                    string changedAt, summary;
                    h.TryGetString("changed_at", out changedAt);
                    h.TryGetString("summary", out summary);
                    history.Add(new FeedTermsHistoryEntry(changedAt, summary));
                }
            }

            string title, rightsHolder, dialect, termsVersion, schemaVersion, creditDisplay;
            string recommendedHashtag, recommendedHashtagNote, scopeNote, structuringNotes;
            string confidence, confidenceBasis, lastVerifiedAt;
            obj.TryGetString("title", out title);
            obj.TryGetString("rights_holder", out rightsHolder);
            obj.TryGetString("dialect", out dialect);
            obj.TryGetString("terms_version", out termsVersion);
            obj.TryGetString("schema_version", out schemaVersion);
            obj.TryGetString("credit_display", out creditDisplay);
            obj.TryGetString("recommended_hashtag", out recommendedHashtag);
            obj.TryGetString("recommended_hashtag_note", out recommendedHashtagNote);
            obj.TryGetString("scope_note", out scopeNote);
            obj.TryGetString("structuring_notes", out structuringNotes);
            obj.TryGetString("confidence", out confidence);
            obj.TryGetString("confidence_basis", out confidenceBasis);
            obj.TryGetString("last_verified_at", out lastVerifiedAt);
            string verification;
            obj.TryGetString("verification", out verification);

            terms = new FeedTerms(termsId, title, rightsHolder, dialect, termsVersion, schemaVersion,
                                  creditDisplay, recommendedHashtag, recommendedHashtagNote, scopeNote,
                                  structuringNotes, confidence, confidenceBasis, lastVerifiedAt,
                                  effectiveDerivation, verification, matrix, overrides, effective, attributes,
                                  conditions, sources, unclearPoints, history);
            return true;
        }
    }
}
