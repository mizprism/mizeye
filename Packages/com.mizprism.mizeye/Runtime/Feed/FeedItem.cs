// items チャンク 1 件分のレコード。
//
// **検証済みチャンクからしか作らない** (internal TryParse を FeedClient だけが呼ぶ)。
// index と同じ規律: 未検証のパース済みデータを呼び出し側に持たせない。
//
// フィールドは v0.1 のレコード実物 (dist/feed/items/*.json) に合わせた最小集合で、
// **未知のフィールドは黙って無視する** (record_schema_versions が上がった時に、古い
// パッケージが「読めない」で全滅しないため)。逆に booth_item_id / url / terms_ref が
// 欠けているレコードは、同定にも原文案内にも使えないので拒否する。
//
// item_conditions を terms に畳まない。共有 terms の値は
// そのままにし、この商品だけの条項は別立てで持つ — 畳むと共有 terms が item ごとに汚れる。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    public sealed class FeedItemCondition
    {
        internal FeedItemCondition(string matrixKey, string note, string quote)
        {
            MatrixKey = matrixKey;
            Note = note;
            Quote = quote;
        }

        /// <summary>permission_matrix のキー (フィールド名は実レコードが正 = matrix_key)。</summary>
        public string MatrixKey { get; }
        public string Note { get; }

        /// <summary>原文からの逐語引用。**各規約の権利者に帰属する** (LICENSING.md)。</summary>
        public string Quote { get; }
    }

    public sealed class FeedItem
    {
        private readonly List<FeedItemCondition> _conditions;

        internal FeedItem(long boothItemId, string name, string shop, string shopSubdomain,
                          string url, string termsRef, string category, string lastVerifiedAt,
                          List<FeedItemCondition> conditions)
        {
            BoothItemId = boothItemId;
            Name = name;
            Shop = shop;
            ShopSubdomain = shopSubdomain;
            Url = url;
            TermsRef = termsRef;
            Category = category;
            LastVerifiedAt = lastVerifiedAt;
            _conditions = conditions;
        }

        public long BoothItemId { get; }
        public string Name { get; }
        public string Shop { get; }
        public string ShopSubdomain { get; }
        public string Url { get; }

        /// <summary>terms エンティティへの参照 ("sha256:…")。チャンクのパスは FeedPath.TermsChunkPath で作る。</summary>
        public string TermsRef { get; }
        public string Category { get; }
        public string LastVerifiedAt { get; }

        /// <summary>この商品だけの条項 (共有 terms には畳まれていない)。</summary>
        public IReadOnlyList<FeedItemCondition> ItemConditions => _conditions;

        internal static bool TryParse(JsonValue obj, out FeedItem item, out string error)
        {
            item = null;
            error = null;
            if (obj == null || obj.Kind != JsonKind.Object)
            {
                error = "item がオブジェクトでない";
                return false;
            }

            long boothItemId;
            string url, termsRef;
            if (!obj.TryGetInt64("booth_item_id", out boothItemId))
            {
                error = "item に booth_item_id が無い";
                return false;
            }
            if (!obj.TryGetString("url", out url))
            {
                error = "item に url が無い (原文へ案内できない)";
                return false;
            }
            if (!obj.TryGetString("terms_ref", out termsRef))
            {
                error = "item に terms_ref が無い (規約を引けない)";
                return false;
            }

            string name, shop, shopSubdomain, category, lastVerifiedAt;
            obj.TryGetString("name", out name);
            obj.TryGetString("shop", out shop);
            obj.TryGetString("shop_subdomain", out shopSubdomain);
            obj.TryGetString("category", out category);
            obj.TryGetString("last_verified_at", out lastVerifiedAt);

            var conditions = new List<FeedItemCondition>();
            JsonValue conditionsArray;
            if (obj.TryGetArray("item_conditions", out conditionsArray))
            {
                for (int i = 0; i < conditionsArray.Items.Count; i++)
                {
                    JsonValue c = conditionsArray.Items[i];
                    if (c.Kind != JsonKind.Object)
                    {
                        error = "item_conditions[" + i + "] がオブジェクトでない";
                        return false;
                    }
                    string matrixKey, note, quote;
                    c.TryGetString("matrix_key", out matrixKey);
                    c.TryGetString("note", out note);
                    c.TryGetString("quote", out quote);
                    conditions.Add(new FeedItemCondition(matrixKey, note, quote));
                }
            }

            item = new FeedItem(boothItemId, name, shop, shopSubdomain, url, termsRef,
                                category, lastVerifiedAt, conditions);
            return true;
        }
    }
}
