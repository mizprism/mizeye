// 時刻の注入口。
//
// なぜインターフェースなのか: レート制限と
// 「最終取得: N 日前」は時刻に依存する挙動であり、時計を直に読むとテストが
// 「24 時間待つ」か「時計を実測しない」かのどちらかになる。前者は不可能、後者は
// 未検証の分岐を残す。

using System;

namespace Mizprism.LicenseLens
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
