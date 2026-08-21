// EditorWindow — 表示層の**描画シェル**だけ。
//
// ## ここに判断を書かないこと (MUST)
//
// このアセンブリは UnityEditor に依存するので、CI (`selftest.yml` の dotnet ジョブ) では
// **1 行もコンパイルされない**。ここに置いた判断は、テストにも netstandard ビルドにも
// 掛からずに利用者へ届く。したがって:
//
//   - 何を表示するか / どう文言化するかは Runtime/View が決める (AssetListView, TermsDetailView,
//     LicenseVocabulary, LinkedAssets)。本ファイルはその結果を GUILayout に流すだけ
//   - 条件分岐を足したくなったら、それは Runtime/View 側に置いてテストを書く合図
//
// ## 保存先 (「プロジェクトを一切変更しない」)
//
// リンク済み商品 ID は **EditorPrefs** に置く。Assets/ に ScriptableObject を書くと、
// 読むだけのはずのツールがプロジェクトを変更したことになる。EditorPrefs はユーザー単位で
// プロジェクト外なので、この前提を壊さない。

using System.Collections.Generic;
using System.IO;
using Mizprism.LicenseLens;
using UnityEditor;
using UnityEngine;

namespace Mizprism.LicenseLens.Editor
{
    public sealed class LicenseLensWindow : EditorWindow
    {
        private const string LinkedPrefsKey = "MIZPRISM.LicenseLens.LinkedItems";
        private const string SourcePrefsKey = "MIZPRISM.LicenseLens.FeedSource";
        private const string ListingRequestUrl = "https://github.com/mizprism/mizeye/issues";

        /// <summary>
        /// 既定の配信元 = 公開フィード。**初回起動で何も設定せずに動く**ことが利用条件で、
        /// 空文字を既定にすると利用者は「どこを指せばいいか判らない画面」を最初に見る。
        /// ローカルディレクトリ指定は開発・検証・オフライン用に残す。
        /// </summary>
        public const string DefaultFeedUrl = "https://feed.mizprism.workers.dev";

        private string _input = string.Empty;
        private string _inputError = string.Empty;
        private string _source = string.Empty;
        private string _sourceError = string.Empty;
        private string _pendingOpen;      // 次フレーム先頭で開く読み込み元 (null = なし)
        private bool _pendingClose;       // 次フレーム先頭で選択画面へ戻す
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private long _selected;

        private FeedClient _client;
        private FeedRefreshResult _lastResult;
        private IReadOnlyList<AssetRowView> _rows = new List<AssetRowView>();
        private FeedStatusView _status;
        private RevisionNoticeView _revisions;

        [MenuItem("Window/MIZPRISM/MizEye")]
        public static void Open() => GetWindow<LicenseLensWindow>("MizEye");

        /// <summary>
        /// データ層を差し込む。ホスト側 (パッケージ利用者側の初期化コード) が
        /// トランスポート・キャッシュ・keyring を組んで渡す。
        /// </summary>
        public void Bind(FeedClient client)
        {
            _client = client;
            Rebuild();
        }

        private void OnEnable()
        {
            // 既定は公開フィード。以前に別の配信元を指していればそちらを尊重する。
            _source = EditorPrefs.GetString(SourcePrefsKey, DefaultFeedUrl);
            if (_source.Length > 0) OpenSource(_source);
        }

        private void OnGUI()
        {
            // **状態の切り替えはフレームの先頭でだけ行う**。IMGUI は同じフレームで Layout と
            // Repaint の 2 回 OnGUI を呼ぶので、ボタンの中で描画する枝を変えると
            // 2 回の呼び出しでレイアウトが食い違い "Mismatched LayoutGroup" になる。
            // ボタンは意図をフラグに置くだけにして、適用はここでまとめる。
            ApplyPendingSourceChange();

            if (_client == null)
            {
                DrawSourcePicker();
                return;
            }

            DrawToolbar();
            DrawBanner();
            DrawRevisionNotice();

            EditorGUILayout.BeginHorizontal();
            DrawList();
            DrawDetail();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 読み込み元がまだ決まっていない時の画面。フィードの公開ホスティングが立つまでは、
        /// ローカルのフィードディレクトリ (リポジトリの dist/feed 等) を指してもらう。
        ///
        /// **ここで診断しない** — ディレクトリが違う / 署名が合わない等は、Refresh の結果が
        /// FeedStatusView.Banner に載って出る (検証済みの経路)。Editor 側で判定を書くと、
        /// CI が見ない場所に二重実装ができる。
        /// </summary>
        private void DrawSourcePicker()
        {
            EditorGUILayout.HelpBox(
                "規約フィードの場所です。通常はこのままで構いません (公開フィードを読みます)。" +
                "オフラインで使う場合や、手元のフィードを検証する場合はローカルディレクトリを指定できます。",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            _source = EditorGUILayout.TextField("フィードの場所", _source);
            if (GUILayout.Button("参照...", GUILayout.Width(70)))
            {
                string picked = EditorUtility.OpenFolderPanel("規約フィードのディレクトリ", _source, string.Empty);
                if (!string.IsNullOrEmpty(picked)) _source = picked;
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("既定 (公開フィード) に戻す")) _source = DefaultFeedUrl;

            EditorGUI.BeginDisabledGroup(_source.Length == 0);
            if (GUILayout.Button("読み込む")) _pendingOpen = _source;
            EditorGUI.EndDisabledGroup();

            if (_sourceError.Length > 0)
                EditorGUILayout.HelpBox(_sourceError, MessageType.Error);
        }

        /// <summary>フレーム先頭でだけ枝を切り替える (上の OnGUI のコメント参照)。</summary>
        private void ApplyPendingSourceChange()
        {
            if (_pendingClose)
            {
                _pendingClose = false;
                _client = null;
                _lastResult = null;
            }
            if (_pendingOpen != null)
            {
                string root = _pendingOpen;
                _pendingOpen = null;
                OpenSource(root);
            }
        }

        /// <summary>データ層を組んで差し込む。判断は持たず、結果は既存の縮退表示に載せる。</summary>
        private void OpenSource(string root)
        {
            // **描画してはいけない** — このメソッドは OnEnable からも呼ばれ、そこは GUI の
            // 文脈ではない。エラーは持ち帰って DrawSourcePicker に出す。
            _sourceError = string.Empty;

            FeedKeyring keyring;
            string keyringError;
            if (!FeedKeyringEmbedded.TryLoad(out keyring, out keyringError))
            {
                // 同梱鍵が読めないのは配布物の破損。取得しても必ず落ちるので先に止める。
                _sourceError = "同梱の公開鍵を読み込めません: " + keyringError;
                return;
            }

            // キャッシュはプロジェクトの外に置く (「プロジェクトを一切変更しない」)。
            string cacheRoot = Path.Combine(Application.persistentDataPath, "MIZPRISM", "LicenseLens", "feed-cache");

            // 配信元の種別で取得口を選ぶ。URL に見えれば HTTP、そうでなければディレクトリ。
            // 判定は Runtime の FeedSource が持つ (engine 非依存なので CI がテストできる)。
            IFeedTransport transport;
            if (FeedSource.LooksLikeHttpUrl(root))
            {
                string urlError;
                string normalized;
                if (!FeedSource.TryNormalizeRoot(root, out normalized, out urlError))
                {
                    _sourceError = urlError;
                    return;
                }
                transport = new FeedHttpTransport(normalized);
            }
            else
            {
                transport = new FeedDirectoryTransport(root);
            }

            _source = root;
            EditorPrefs.SetString(SourcePrefsKey, root);
            _client = new FeedClient(transport, new FeedCache(cacheRoot),
                                     keyring, SystemClock.Instance);
            _lastResult = _client.Refresh(manual: true);
            Rebuild();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            _input = EditorGUILayout.TextField(_input, EditorStyles.toolbarTextField);
            if (GUILayout.Button("リンク", EditorStyles.toolbarButton, GUILayout.Width(60))) AddLink();

            GUILayout.FlexibleSpace();

            if (_status != null) GUILayout.Label(_status.FreshnessLabel, EditorStyles.miniLabel);
            if (GUILayout.Button("更新", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                _lastResult = _client.Refresh(manual: true);
                Rebuild();
            }
            if (GUILayout.Button("読み込み元", EditorStyles.toolbarButton, GUILayout.Width(80)))
                _pendingClose = true;   // 選択画面に戻す (適用は次フレーム先頭、キャッシュは残る)

            EditorGUILayout.EndHorizontal();

            if (_inputError.Length > 0)
                EditorGUILayout.HelpBox(_inputError, MessageType.Warning);
        }

        private void DrawBanner()
        {
            if (_status == null || !_status.HasBanner) return;
            EditorGUILayout.HelpBox(_status.Banner, MessageType.Warning);
        }

        private void DrawRevisionNotice()
        {
            if (_revisions == null || !_revisions.HasRevisions) return;
            EditorGUILayout.HelpBox(_revisions.Summary, MessageType.Info);
        }

        private void DrawList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(280));
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);

            for (int i = 0; i < _rows.Count; i++)
            {
                AssetRowView row = _rows[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                bool selected = row.BoothItemId == _selected;
                if (GUILayout.Toggle(selected, row.DisplayName, EditorStyles.label) && !selected)
                    _selected = row.BoothItemId;
                if (GUILayout.Button("×", GUILayout.Width(22))) RemoveLink(row.BoothItemId);
                EditorGUILayout.EndHorizontal();

                GUILayout.Label(row.StatusLabel + (row.Revised ? "  ● 規約が改訂されました" : string.Empty),
                                EditorStyles.miniLabel);
                if (row.Shop.Length > 0) GUILayout.Label(row.Shop, EditorStyles.miniLabel);
                if (row.ShowListingRequestLink && GUILayout.Button("収載をリクエスト", EditorStyles.miniButton))
                    Application.OpenURL(ListingRequestUrl);

                EditorGUILayout.EndVertical();
            }

            if (_rows.Count == 0)
                EditorGUILayout.HelpBox("BOOTH の商品 URL か商品 ID を貼り付けてリンクしてください。", MessageType.Info);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawDetail()
        {
            EditorGUILayout.BeginVertical();
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

            AssetRowView row = FindSelected();
            TermsDetailView detail = row != null ? row.BuildDetail() : null;
            if (detail == null)
            {
                if (row != null && row.Reason.Length > 0) EditorGUILayout.HelpBox(row.Reason, MessageType.Info);
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
                return;
            }

            GUILayout.Label(detail.Title, EditorStyles.boldLabel);
            GUILayout.Label("権利者: " + detail.RightsHolder, EditorStyles.miniLabel);

            GUILayout.Space(6);
            for (int i = 0; i < detail.Attributes.Count; i++)
                GUILayout.Label(detail.Attributes[i].Line);

            if (detail.HasItemConditions)
            {
                GUILayout.Space(6);
                EditorGUILayout.HelpBox(detail.ItemConditionsNotice, MessageType.Warning);
                DrawConditions(detail.ItemConditions);
            }

            if (detail.Conditions.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("条件", EditorStyles.boldLabel);
                DrawConditions(detail.Conditions);
            }

            if (detail.HasUnclearPoints)
            {
                GUILayout.Space(6);
                GUILayout.Label("規約から判断できない点", EditorStyles.boldLabel);
                for (int i = 0; i < detail.UnclearPoints.Count; i++)
                    GUILayout.Label("・" + detail.UnclearPoints[i], EditorStyles.wordWrappedMiniLabel);
            }

            GUILayout.Space(6);
            GUILayout.Label("原文", EditorStyles.boldLabel);
            for (int i = 0; i < detail.Sources.Count; i++)
            {
                SourceView s = detail.Sources[i];
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(s.RoleLabel + " (取得 " + s.FetchedAt + ")", EditorStyles.miniLabel);
                if (s.Url.Length > 0 && GUILayout.Button("開く", EditorStyles.miniButton, GUILayout.Width(50)))
                    Application.OpenURL(s.Url);
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.Label("構造化の確度: " + detail.Confidence, EditorStyles.miniLabel);
            GUILayout.Label(detail.ConfidenceBasis, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private static void DrawConditions(IReadOnlyList<ConditionView> conditions)
        {
            for (int i = 0; i < conditions.Count; i++)
            {
                ConditionView c = conditions[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label(c.MatrixLabel, EditorStyles.miniBoldLabel);
                if (c.Note.Length > 0) GUILayout.Label(c.Note, EditorStyles.wordWrappedMiniLabel);
                if (c.HasQuote) GUILayout.Label("「" + c.Quote + "」", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private AssetRowView FindSelected()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].BoothItemId == _selected) return _rows[i];
            return _rows.Count > 0 ? _rows[0] : null;
        }

        // ---- 状態の出し入れ (判断は Runtime/View 側) ----

        private void AddLink()
        {
            long id;
            if (!LinkedAssets.TryParseItemReference(_input, out id))
            {
                _inputError = "BOOTH の商品 URL か商品 ID として読めませんでした。";
                return;
            }
            _inputError = string.Empty;
            _input = string.Empty;

            var ids = new List<long>(LoadLinked()) { id };
            SaveLinked(ids);
            _selected = id;
            Rebuild();
        }

        private void RemoveLink(long boothItemId)
        {
            var kept = new List<long>();
            IReadOnlyList<long> current = LoadLinked();
            for (int i = 0; i < current.Count; i++)
                if (current[i] != boothItemId) kept.Add(current[i]);
            SaveLinked(kept);
            Rebuild();
        }

        private static IReadOnlyList<long> LoadLinked() =>
            LinkedAssets.Parse(EditorPrefs.GetString(LinkedPrefsKey, string.Empty));

        private static void SaveLinked(IEnumerable<long> ids) =>
            EditorPrefs.SetString(LinkedPrefsKey, LinkedAssets.Format(ids));

        private void Rebuild()
        {
            if (_client == null) return;
            IReadOnlyList<string> changed = _lastResult != null && _lastResult.Diff != null
                ? _lastResult.Diff.ChangedTermsIds
                : null;
            _rows = AssetListView.BuildRows(_client, LoadLinked(), changed);
            _status = AssetListView.BuildStatus(_client, _lastResult);
            _revisions = AssetListView.BuildRevisionNotice(
                _rows, _lastResult != null ? _lastResult.Diff : null);
            Repaint();
        }
    }
}
