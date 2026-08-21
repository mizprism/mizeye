// 詳細ペイン: 属性・条件・原文リンク・確度・unclear 事項。
//
// 表示層の判断を全部ここに置く — EditorWindow 側は本クラスの結果を描くだけにする。
// Editor アセンブリは CI でコンパイルされない (UnityEditor 依存) ので、そこに判断を
// 置いた分だけ「配ってから気付く」面積が増える。
//
// 守る不変条件 (いずれも帰結で、破ると製品が別物になる):
//
//  1. **属性は 7 つ必ず出す**。フィードに無い属性は Unknown として並べる — 欠測を
//     「表示しない」で処理すると、画面には残った属性だけが並び、利用者はそれを全体だと
//     読む (欠測が最良ケースとして採点される型の誤り)。
//  2. **item 固有条項を terms の属性に畳まない**。共有 terms の値は
//     そのままに、item 固有条項は**別の節**として併記する。畳むと、同じ terms を参照する
//     他の商品にまで条件が伝播して見える。
//  3. **原文へ戻る手段を必ず添える** — sources (URL + 取得日時 + hash)、条件の引用、確度。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    /// <summary>条件 1 件 (terms 共有 / item 固有のどちらも同じ形で見せる)。</summary>
    public sealed class ConditionView
    {
        internal ConditionView(string matrixKey, string note, string quote)
        {
            MatrixKey = matrixKey ?? string.Empty;
            Note = note ?? string.Empty;
            Quote = quote ?? string.Empty;
        }

        public string MatrixKey { get; }

        /// <summary>この条件がどの行に付くかの日本語ラベル。</summary>
        public string MatrixLabel => LicenseVocabulary.MatrixLabel(MatrixKey);

        public string Note { get; }

        /// <summary>原文からの最小引用。空なら引用が採録されていない (推測で補わない)。</summary>
        public string Quote { get; }

        public bool HasQuote => Quote.Length > 0;
    }

    /// <summary>一次資料 1 本。監査可能性のために URL・取得日時・hash を必ず持つ。</summary>
    public sealed class SourceView
    {
        internal SourceView(string url, string role, string locationType,
                            string fetchedAt, string contentHash, long? pages, string note)
        {
            Url = url ?? string.Empty;
            Role = role ?? string.Empty;
            LocationType = locationType ?? string.Empty;
            FetchedAt = fetchedAt ?? string.Empty;
            ContentHash = contentHash ?? string.Empty;
            Pages = pages;
            Note = note ?? string.Empty;
        }

        public string Url { get; }
        public string Role { get; }
        public string LocationType { get; }
        public string FetchedAt { get; }
        public string ContentHash { get; }
        public long? Pages { get; }
        public string Note { get; }

        /// <summary>primary = 規約本体。それ以外は補助資料であることを利用者に示す。</summary>
        public bool IsPrimary => string.Equals(Role, "primary", StringComparison.Ordinal);

        public string RoleLabel
        {
            get
            {
                switch (Role)
                {
                    case "primary": return "規約本体";
                    case "secondary": return "補助資料";
                    case "context": return "参考";
                    default: return Role.Length == 0 ? "(役割不明)" : Role;
                }
            }
        }
    }

    /// <summary>詳細ペインの中身。EditorWindow はこれを描くだけ。</summary>
    public sealed class TermsDetailView
    {
        private readonly List<LicenseAttributeView> _attributes;
        private readonly List<ConditionView> _conditions;
        private readonly List<ConditionView> _itemConditions;
        private readonly List<SourceView> _sources;
        private readonly List<string> _unclearPoints;

        private TermsDetailView(string title, string rightsHolder, string dialect, string termsVersion,
                                string creditDisplay, string scopeNote, string structuringNotes,
                                string confidence, string confidenceBasis, string lastVerifiedAt,
                                List<LicenseAttributeView> attributes, List<ConditionView> conditions,
                                List<ConditionView> itemConditions, List<SourceView> sources,
                                List<string> unclearPoints)
        {
            Title = title ?? string.Empty;
            RightsHolder = rightsHolder ?? string.Empty;
            Dialect = dialect ?? string.Empty;
            TermsVersion = termsVersion ?? string.Empty;
            CreditDisplay = creditDisplay ?? string.Empty;
            ScopeNote = scopeNote ?? string.Empty;
            StructuringNotes = structuringNotes ?? string.Empty;
            Confidence = confidence ?? string.Empty;
            ConfidenceBasis = confidenceBasis ?? string.Empty;
            LastVerifiedAt = lastVerifiedAt ?? string.Empty;
            _attributes = attributes;
            _conditions = conditions;
            _itemConditions = itemConditions;
            _sources = sources;
            _unclearPoints = unclearPoints;
        }

        public string Title { get; }
        public string RightsHolder { get; }
        public string Dialect { get; }
        public string TermsVersion { get; }

        /// <summary>クレジットに書く表記 (規約が指定している場合)。</summary>
        public string CreditDisplay { get; }

        public string ScopeNote { get; }
        public string StructuringNotes { get; }

        /// <summary>構造化の確度。値そのものと、その根拠を必ず対で出す。</summary>
        public string Confidence { get; }
        public string ConfidenceBasis { get; }

        public string LastVerifiedAt { get; }

        /// <summary>7 属性。フィードに無いものも Unknown として必ず並ぶ。</summary>
        public IReadOnlyList<LicenseAttributeView> Attributes => _attributes;

        /// <summary>共有 terms に付く条件。</summary>
        public IReadOnlyList<ConditionView> Conditions => _conditions;

        /// <summary>
        /// この商品にだけ付く条項。**共有 terms の属性には畳まれていない** (共有の値を個別条項で汚さないため)。
        /// 非空なら、属性だけを見て判断すると足りないことを画面で明示する。
        /// </summary>
        public IReadOnlyList<ConditionView> ItemConditions => _itemConditions;

        public IReadOnlyList<SourceView> Sources => _sources;
        public IReadOnlyList<string> UnclearPoints => _unclearPoints;

        public bool HasItemConditions => _itemConditions.Count > 0;
        public bool HasUnclearPoints => _unclearPoints.Count > 0;

        /// <summary>
        /// item 固有条項がある時に画面へ出す注意書き。属性の一覧だけで判断を終えられると
        /// 誤るので、併読が要ることを言葉で示す。
        /// </summary>
        public string ItemConditionsNotice =>
            HasItemConditions
                ? "この商品だけの条項が " + _itemConditions.Count + " 件あります — 共有規約には畳まれていないので併せて読んでください"
                : string.Empty;

        /// <summary>
        /// 規約本体の URL (primary が複数あれば最初の 1 本)。無ければ空。
        /// 「原文を開く」導線はここが唯一の根拠。
        /// </summary>
        public string PrimarySourceUrl
        {
            get
            {
                for (int i = 0; i < _sources.Count; i++)
                    if (_sources[i].IsPrimary && _sources[i].Url.Length > 0) return _sources[i].Url;
                return string.Empty;
            }
        }

        /// <summary>
        /// terms (+ 任意で item) から詳細ペインを組み立てる。
        /// item を渡すと item_conditions が別節として載る。
        /// </summary>
        public static TermsDetailView From(FeedTerms terms, FeedItem item)
        {
            if (terms == null) throw new ArgumentNullException(nameof(terms));

            var attributes = new List<LicenseAttributeView>();
            IReadOnlyList<string> order = LicenseVocabulary.OrderedAttributeKeys;
            for (int i = 0; i < order.Count; i++)
            {
                string key = order[i];
                string raw;
                // 欠落は「並べない」ではなく Unknown として並べる (不変条件 1)。
                if (terms.AttributesDerived == null || !terms.AttributesDerived.TryGetValue(key, out raw))
                    raw = null;
                attributes.Add(new LicenseAttributeView(
                    key, LicenseVocabulary.AttributeLabel(key), LicenseVocabulary.Parse(raw), raw));
            }

            var conditions = new List<ConditionView>();
            if (terms.Conditions != null)
            {
                for (int i = 0; i < terms.Conditions.Count; i++)
                {
                    FeedTermsCondition c = terms.Conditions[i];
                    conditions.Add(new ConditionView(c.MatrixKey, c.Note, c.Quote));
                }
            }

            var itemConditions = new List<ConditionView>();
            if (item != null && item.ItemConditions != null)
            {
                for (int i = 0; i < item.ItemConditions.Count; i++)
                {
                    FeedItemCondition c = item.ItemConditions[i];
                    itemConditions.Add(new ConditionView(c.MatrixKey, c.Note, c.Quote));
                }
            }

            var sources = new List<SourceView>();
            if (terms.Sources != null)
            {
                for (int i = 0; i < terms.Sources.Count; i++)
                {
                    FeedTermsSource s = terms.Sources[i];
                    sources.Add(new SourceView(s.Url, s.Role, s.LocationType,
                                               s.FetchedAt, s.ContentHash, s.Pages, s.Note));
                }
            }

            var unclear = new List<string>();
            if (terms.UnclearPoints != null)
            {
                for (int i = 0; i < terms.UnclearPoints.Count; i++)
                {
                    string p = terms.UnclearPoints[i];
                    if (!string.IsNullOrEmpty(p)) unclear.Add(p);
                }
            }

            return new TermsDetailView(
                terms.Title, terms.RightsHolder, terms.Dialect, terms.TermsVersion,
                terms.CreditDisplay, terms.ScopeNote, terms.StructuringNotes,
                terms.Confidence, terms.ConfidenceBasis, terms.LastVerifiedAt,
                attributes, conditions, itemConditions, sources, unclear);
        }
    }
}
