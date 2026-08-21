// 表示層の語彙 — スキーマの値を、利用者が読む日本語に写す 1 ヶ所。
//
// ここが表示層で最も製品思想に近い。「判定ではなく整理」の帰結:
//
//  1. **`unclear` は第一級の値**。空欄にも「情報なし」にもしない。規約に書かれていない
//     ことが判っている、という積極的な事実として出す。
//  2. **知らない値を安全側に丸めない**。フィードが将来 v1.3 で値を足した時、古いビューアが
//     それを `allowed` 相当に見せたら、利用者は許諾されていない利用に踏み込む。知らない値は
//     Unknown に落として**原文の文字列をそのまま併記**する (握り潰さない)。
//  3. **既定値 (0) は安全な側** — FeedStructureGuardTests.SafeValuesSitAtZero と同じ規律。
//     初期化し忘れた `LicenseValue` が「許可」として表示される経路を作らない。
//
// 判定を返さないので、ここには「使える/使えない」を意味する語を置かない。値の日本語訳と、
// 原文確認を促す定型句だけを持つ。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    /// <summary>
    /// スキーマの属性値。**Unknown が 0** — 判らないものを許可として表示しないため。
    /// NotRequired / Required は credit_required (V) 専用値。
    /// </summary>
    public enum LicenseValue
    {
        /// <summary>このビューアが知らない値。原文の文字列を併記して出す。</summary>
        Unknown = 0,
        /// <summary>規約に記載がない / 解釈できない。第一級の値であって欠測ではない。</summary>
        Unclear,
        Allowed,
        Conditional,
        Forbidden,
        NotRequired,
        Required
    }

    /// <summary>
    /// 属性 1 つの表示単位。**RawValue を必ず保持する** — 語彙を知らない値でも、
    /// 利用者は原文の語を見て自分で判断できる。
    /// </summary>
    public sealed class LicenseAttributeView
    {
        internal LicenseAttributeView(string key, string label, LicenseValue value, string rawValue)
        {
            Key = key;
            Label = label;
            Value = value;
            RawValue = rawValue ?? string.Empty;
        }

        /// <summary>スキーマのキー (例: "commercial_use")。</summary>
        public string Key { get; }

        /// <summary>利用者に見せる日本語のラベル (例: "商用利用")。</summary>
        public string Label { get; }

        public LicenseValue Value { get; }

        /// <summary>フィードにあった値の文字列そのもの。知らない値の時ここだけが手がかりになる。</summary>
        public string RawValue { get; }

        /// <summary>値の日本語表記。知らない値は「不明な値 (&lt;原文&gt;)」と正直に出す。</summary>
        public string ValueLabel => LicenseVocabulary.ValueLabel(Value, RawValue);

        /// <summary>
        /// 出力形式そのもの:
        /// 「該当条項: 商用利用 = 条件付き — 原文の該当箇所を確認」。
        /// </summary>
        public string Line => "該当条項: " + Label + " = " + ValueLabel + " — " + LicenseVocabulary.ConfirmSuffix;
    }

    public static class LicenseVocabulary
    {
        /// <summary>全表示に添える定型句。判定ではなく整理であることを、文面で毎回示す。</summary>
        public const string ConfirmSuffix = "原文の該当箇所を確認";

        /// <summary>
        /// 詳細ペインに出す属性の**固定順**。フィードの辞書順や欠落に表示順を左右させない
        /// (順序が揺れると、利用者は「前回と違う」を規約改訂と読み違える)。
        /// </summary>
        private static readonly string[] AttributeOrder =
        {
            "commercial_use",
            "monetized_streaming",
            "avatar_modification",
            "commissioned_editing",
            "parts_reuse",
            "redistribution",
            "credit_required"
        };

        private static readonly Dictionary<string, string> AttributeLabels =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commercial_use", "商用利用" },
                { "monetized_streaming", "収益化配信" },
                { "avatar_modification", "アバターの改変" },
                { "commissioned_editing", "改変の外注" },
                { "parts_reuse", "パーツ流用" },
                { "redistribution", "再配布" },
                { "credit_required", "クレジット表記" }
            };

        /// <summary>
        /// permission_matrix の行キー (A..X) の日本語ラベル。条件の帰属先を示すのに使う。
        ///
        /// 表記は**規約側の許諾表の行名に合わせている** — 利用者が画面の行を規約本文の行と
        /// 突き合わせられるようにするため。勝手な言い換えを増やすと、突き合わせが利用者の負担になる。
        /// </summary>
        private static readonly Dictionary<string, string> MatrixLabels =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // 1. 利用主体
                { "A_individual_use", "個人利用" },
                { "B_corporate_use", "法人利用" },
                // 2. オンラインサービスへのアップロード
                { "C_upload_social_platform", "ソーシャルコミュニケーションプラットフォームへのアップロード" },
                { "D_upload_game_platform", "オンラインゲームプラットフォームへのアップロード" },
                { "E_upload_for_third_party_use", "オンラインサービス内での第三者への利用の許諾" },
                // 3. センシティブな表現
                { "F_sexual_expression", "性的表現" },
                { "G_violent_expression", "暴力的表現" },
                { "H_political_religious", "政治活動・宗教活動" },
                // 4. 加工
                { "I_adjustment", "調整" },
                { "J_modification", "改変" },
                { "K_use_for_modifying_other_data", "他のデータを改変するための利用" },
                { "L_outsource_modification", "調整・改変の外部委託" },
                // 5. 再配布・配布
                { "M_redistribute_unmodified", "未改変状態での再配布" },
                { "N_distribute_modified", "改変したデータの配布" },
                // 6. メディア・プロダクトへの使用
                { "O_video_streaming_broadcast", "映像作品・配信・放送への利用" },
                { "P_publishing", "出版物・電子出版物への利用" },
                { "Q_physical_goods", "有体物（グッズ）への利用" },
                { "R_product_development", "製品開発等のためのソフトウェアへの組み込み" },
                // 7. 二次創作
                { "S_outfit_data_with_mesh_weight", "メッシュやウェイトを転用した衣装データの作成" },
                { "T_outfit_data_without_mesh_weight", "メッシュやウェイトを転用しない衣装データ・テクスチャの作成" },
                { "U_derivative_works", "データをモチーフにした二次的著作物（二次創作）の作成" },
                // 8. その他
                { "V_credit", "クレジット表記" },
                { "W_transfer_rights", "権利義務の譲渡等" },
                // 9. 特記事項
                { "X_special_notes", "特記事項" }
            };

        /// <summary>
        /// 構造化の確度 (スキーマの enum: high / medium / low) の日本語表記。
        ///
        /// **知らない値はそのまま返す** — 属性値と同じ規律で、握り潰すと利用者は
        /// 「確度の情報が無い」と読むが、実際には情報はあってビューアが古いだけになる。
        /// スキーマが値を足した時にここが赤くなるよう、収録データ全件に対する検査を
        /// 適合スイート側に置いてある (条項ラベルと同型)。
        /// </summary>
        private static readonly Dictionary<string, string> ConfidenceLabels =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "high", "高" },
                { "medium", "中" },
                { "low", "低" }
            };

        public static string ConfidenceLabel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string label;
            return ConfidenceLabels.TryGetValue(raw, out label) ? label : raw;
        }

        /// <summary>属性の表示順 (固定)。</summary>
        public static IReadOnlyList<string> OrderedAttributeKeys => AttributeOrder;

        public static string AttributeLabel(string key)
        {
            if (key == null) return string.Empty;
            string label;
            return AttributeLabels.TryGetValue(key, out label) ? label : key;
        }

        /// <summary>
        /// 行キーのラベル。**知らないキーはキー名をそのまま返す** — 表示から消すと、
        /// 条件が付いている事実まで一緒に消える。
        /// </summary>
        public static string MatrixLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            string label;
            return MatrixLabels.TryGetValue(key, out label) ? label : key;
        }

        /// <summary>スキーマの値文字列 → 語彙。知らない語は Unknown (安全側)。</summary>
        public static LicenseValue Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return LicenseValue.Unknown;
            switch (raw)
            {
                case "allowed": return LicenseValue.Allowed;
                case "conditional": return LicenseValue.Conditional;
                case "forbidden": return LicenseValue.Forbidden;
                case "unclear": return LicenseValue.Unclear;
                case "not_required": return LicenseValue.NotRequired;
                case "required": return LicenseValue.Required;
                default: return LicenseValue.Unknown;
            }
        }

        public static string ValueLabel(LicenseValue value, string rawValue)
        {
            switch (value)
            {
                case LicenseValue.Allowed: return "許可";
                case LicenseValue.Conditional: return "条件付き";
                case LicenseValue.Forbidden: return "禁止";
                case LicenseValue.Unclear: return "規約に記載なし / 解釈不能";
                case LicenseValue.NotRequired: return "不要";
                case LicenseValue.Required: return "必要";
                default:
                    // 知らない値。**原文の語を必ず見せる** — ここで握り潰すと、利用者は
                    // 「情報が無い」と読むが、実際には情報はあってビューアが古いだけ。
                    return string.IsNullOrEmpty(rawValue)
                        ? "不明な値"
                        : "不明な値 (" + rawValue + ")";
            }
        }
    }
}
