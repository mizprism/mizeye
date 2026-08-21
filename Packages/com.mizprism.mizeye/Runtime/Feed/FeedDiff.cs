// 前回取得との差分。
//
// 差分はチャンクの **content hash** で取る。フィードは内容アドレスなので、
// 「hash が同じ = 中身が同じ」が成り立ち、本文を読まずに改訂を検出できる。逆に言うと、
// hash が違えば必ず何かが変わっている — 表示層はここを起点に本文を読み比べればよい。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    public sealed class FeedDiff
    {
        private readonly List<string> _added;
        private readonly List<string> _removed;
        private readonly List<string> _changed;
        private readonly List<string> _changedTermsIds;

        private FeedDiff(List<string> added, List<string> removed, List<string> changed,
                         List<string> changedTermsIds, bool dataAsOfChanged)
        {
            _added = added;
            _removed = removed;
            _changed = changed;
            _changedTermsIds = changedTermsIds;
            DataAsOfChanged = dataAsOfChanged;
        }

        /// <summary>新しく載ったチャンク (新規収載)。</summary>
        public IReadOnlyList<string> AddedChunks => _added;

        /// <summary>消えたチャンク。</summary>
        public IReadOnlyList<string> RemovedChunks => _removed;

        /// <summary>両方にあって hash が違うチャンク (= 改訂)。</summary>
        public IReadOnlyList<string> ChangedChunks => _changed;

        /// <summary>
        /// 改訂された terms の id 断片 ("terms/&lt;hex&gt;.json" のファイル名の幹)。
        /// **追加は含めない** — 新しい terms チャンクは新規収載であって規約の改訂ではなく、
        /// 「規約が変わった」という通知に混ぜると利用者への意味が変わる。
        /// </summary>
        public IReadOnlyList<string> ChangedTermsIds => _changedTermsIds;

        public bool DataAsOfChanged { get; }

        public bool IsEmpty =>
            _added.Count == 0 && _removed.Count == 0 && _changed.Count == 0 && !DataAsOfChanged;

        /// <summary>previous が null (キャッシュが空だった) 場合は全チャンクが Added になる。</summary>
        public static FeedDiff Between(FeedIndex previous, FeedIndex current)
        {
            var added = new List<string>();
            var removed = new List<string>();
            var changed = new List<string>();
            var changedTermsIds = new List<string>();

            if (current == null)
            {
                // 比較対象が無いなら差分も無い (呼び出し側が「取得できなかった」を別途表す)。
                return new FeedDiff(added, removed, changed, changedTermsIds, false);
            }

            for (int i = 0; i < current.Chunks.Count; i++)
            {
                FeedChunkEntry entry = current.Chunks[i];
                FeedChunkEntry before;
                if (previous == null || !previous.TryGetChunk(entry.Path, out before))
                {
                    added.Add(entry.Path);
                    continue;
                }
                if (!string.Equals(before.Sha256, entry.Sha256, StringComparison.Ordinal))
                {
                    changed.Add(entry.Path);
                    string termsId = FeedPath.TermsIdFromChunkPath(entry.Path);
                    if (termsId != null) changedTermsIds.Add(termsId);
                }
            }

            if (previous != null)
            {
                for (int i = 0; i < previous.Chunks.Count; i++)
                {
                    FeedChunkEntry entry = previous.Chunks[i];
                    FeedChunkEntry after;
                    if (!current.TryGetChunk(entry.Path, out after)) removed.Add(entry.Path);
                }
            }

            // previous が無い時の DataAsOfChanged は true にする (「前回と同じ」と言える前回が無い)。
            bool dataAsOfChanged = previous == null ||
                                   !string.Equals(previous.DataAsOf, current.DataAsOf, StringComparison.Ordinal);

            added.Sort(StringComparer.Ordinal);
            removed.Sort(StringComparer.Ordinal);
            changed.Sort(StringComparer.Ordinal);
            changedTermsIds.Sort(StringComparer.Ordinal);

            return new FeedDiff(added, removed, changed, changedTermsIds, dataAsOfChanged);
        }
    }
}
