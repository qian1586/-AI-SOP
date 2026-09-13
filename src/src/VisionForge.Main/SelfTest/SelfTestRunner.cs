using System.IO;
using System.Text;
using VisionForge.Common.Events;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Hardware.Alarm;
using VisionForge.Hardware.Camera;
using VisionForge.Hardware.Plc;
using VisionForge.Hardware.Simulation;
using VisionForge.Hardware.Vision;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Integration;
using VisionForge.Infrastructure.Logging;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.Imaging;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main.SelfTest;

/// <summary>
/// 监测逻辑自检 —— 不开界面，把整套流程真跑一遍，输出可核对的报告。
///
/// <para><b>它验证的是"人做错了能不能拦住"，不是"程序能不能启动"：</b></para>
/// <list type="number">
///   <item>规范作业 → 每道工序通过、最后整件完成、计数正确</item>
///   <item>跳步 → 判 NG、锁流程、停在原步骤</item>
///   <item>补做被跳过的工序 → 复检通过、自动解锁、继续往下走</item>
///   <item>漏装/未动作就触发 → 判"进行中"，不推进、不计数</item>
///   <item>重复触发 → 幂等，不重复计数</item>
///   <item>人工放行 → 推进并被计数（评估算法是否合格的关键指标）</item>
///   <item>每次触发都落历史 → 可追溯</item>
///   <item>复位后重新开始一件 → 状态机与算法记忆都被清干净</item>
/// </list>
///
/// <para>用的图像来自可编程场景相机：场景由动作模拟器构造，
/// 但取图、算法、状态机、计数、落盘全都是生产代码本身 —— 所以自检通过，
/// 现场那条链路就是通过的。</para>
/// </summary>
public sealed class SelfTestRunner
{
    private sealed class CaseResult
    {
        public string Name { get; init; } = string.Empty;
        public bool Passed { get; init; }

        /// <summary>跳过：需要现场硬件或功能尚未开发。不计入通过，也不计入失败。</summary>
        public bool Skipped { get; init; }

        public string Detail { get; init; } = string.Empty;
    }

    private readonly List<CaseResult> _cases = new();
    private readonly StringBuilder _trace = new();
    private int _triggers;
    private int _milestones;

    /// <summary>跑一遍自检。返回 0 = 全部通过，1 = 有失败项。</summary>
    public static int Run(string baseDir, string? reportPath)
    {
        return new SelfTestRunner().Execute(baseDir, reportPath);
    }

    private int Execute(string baseDir, string? reportPath)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string root = Path.Combine(baseDir, "data", "selftest-" + stamp);
        string recipeDir = Path.Combine(root, "recipes");
        string historyDir = Path.Combine(root, "history");
        string ngDir = Path.Combine(root, "ng-images");

        Directory.CreateDirectory(recipeDir);
        Directory.CreateDirectory(historyDir);
        Directory.CreateDirectory(ngDir);

        var recipes = new JsonRecipeRepository(recipeDir);
        var history = new JsonLineHistoryStore(historyDir, ngDir);
        var registry = AlgorithmRegistry.CreateDefault();

        var camera = new MockCamera(1280, 1024, 42, "自检用模拟相机");
        camera.ConnectAsync().GetAwaiter().GetResult();

        var simulator = new OperatorActionSimulator(camera);

        var recipe = DemoDataFactory.CreateDemoRecipe("CAM-SELFTEST");

        // 自检自己造 6 个框：
        // 示例配方现在**不预置任何框**（现场要求"框必须自己建"），
        // 但保底层这组用例要验证"ROI 顺序 = 工序顺序"的判定链路，必须得有框。
        // 所以框由自检自己造，不再依赖示例数据 —— 示例数据以后怎么改都不影响这里。
        for (int i = 0; i < 6; i++)
        {
            recipe.Rois.Add(new RoiRegion
            {
                Name = $"自检框{i + 1}",
                X = 0.06 + (i % 3) * 0.32,
                Y = i < 3 ? 0.10 : 0.55,
                Width = 0.24,
                Height = 0.33,
                Enabled = true,
            });
        }

        recipes.SaveAsync(recipe).GetAwaiter().GetResult();

        var sopDefinition = DemoDataFactory.CreateDemoSop(recipe.Id);
        var process = new SopProcess();
        process.Load(sopDefinition);

        var session = new MonitoringSession(process);
        var algorithm = registry.Find(recipe.AlgorithmKey);

        if (algorithm is null)
        {
            _cases.Add(new CaseResult
            {
                Name = "T0 算法插件注册",
                Passed = false,
                Detail = $"找不到算法「{recipe.AlgorithmKey}」",
            });
            return Finish(root, reportPath);
        }

        int settleFrames = Math.Clamp(
            new ParameterReader(recipe, algorithm.Parameters).GetInt("DebounceFrames", 1), 1, 8);

        // ---- 一次完整触发：模拟动作 → 抓图 → 算法 → 监测会话 → 落历史 ----
        MonitoringOutcome Trigger(SimulatedAction action)
        {
            string actionText = simulator.Perform(recipe, action);
            _triggers++;

            var frame = camera.GrabOnceAsync(2000).GetAwaiter().GetResult();
            if (frame is null) throw new InvalidOperationException("取图失败：模拟相机没有返回图像");

            InspectionResult? last = null;
            for (int i = 0; i < settleFrames; i++)
                last = algorithm.Run(frame, recipe);

            last!.BatchNo = "SELFTEST";
            last.Operator = "自检";

            var outcome = session.Process(last);

            // 和界面保持一致：只有"有结论"的触发（OK / NG）才落历史。
            // "进行中"不是结论，记进去只会把历史表刷成噪音。
            if (last.Verdict == InspectionVerdict.Pass || last.Verdict == InspectionVerdict.Fail)
            {
                _milestones++;
                history.SaveAsync(InspectionRecord.FromResult(last)).GetAwaiter().GetResult();
            }

            _trace.AppendLine($"  · {actionText}");
            _trace.AppendLine($"    判定 {last.VerdictText}（进度 {last.ProgressSteps}/{recipe.Rois.Count}）：{last.JudgementReason}");
            _trace.AppendLine($"    流程 {outcome.Advance}；OK {session.OkCount} / NG {session.NgCount} / 完成 {session.CompletedPieces} 件");

            return outcome;
        }

        void Check(string name, bool passed, string detail) =>
            _cases.Add(new CaseResult { Name = name, Passed = passed, Detail = detail });

        // 需要现场硬件、或对应功能还没开发的用例：如实标"跳过"，不假装通过
        void Note(string name, string detail) =>
            _cases.Add(new CaseResult { Name = name, Passed = true, Skipped = true, Detail = detail });

        simulator.Reset(recipe);

        // ---------------- T1：规范作业 ----------------
        var r1 = Trigger(SimulatedAction.CorrectStep);
        Check("T1 规范完成第 1 道工序 → 判定 OK 并推进到第 2 道",
            r1.Result.Verdict == InspectionVerdict.Pass
            && r1.Advance == AdvanceOutcome.Advanced
            && process.CurrentStep?.Seq == 2
            && session.OkCount == 1,
            $"判定={r1.Result.VerdictText}，流程={r1.Advance}，当前步骤={process.CurrentStepText}，OK={session.OkCount}");

        // ---------------- T2：剩余工序依次通过 ----------------
        AdvanceOutcome lastAdvance = r1.Advance;
        for (int i = 0; i < recipe.Rois.Count - 1; i++)
        {
            var o = Trigger(SimulatedAction.CorrectStep);
            lastAdvance = o.Advance;
        }

        Check("T2 六道工序全部按序完成 → 整件完成、件数 +1",
            lastAdvance == AdvanceOutcome.ProcessCompleted
            && process.AllPassed
            && session.CompletedPieces == 1
            && session.OkCount == recipe.Rois.Count,
            $"最终流程={lastAdvance}，OK={session.OkCount}，完成件数={session.CompletedPieces}，全部通过={process.AllPassed}");

        // ---------------- T3：跳步 ----------------
        process.Reset();
        session.ResetForNewPiece();
        simulator.Reset(recipe);
        if (algorithm is SopSequenceAlgorithm seqReset) seqReset.ResetAll();

        var r3 = Trigger(SimulatedAction.SkipStep);
        bool skipCaught = r3.Result.Verdict == InspectionVerdict.Fail
                          && r3.Result.JudgementReason.Contains("跳步")
                          && process.IsLocked
                          && process.CurrentStep?.Seq == 1;
        Check("T3 跳步 → 判 NG、锁流程、仍停在第 1 道工序", skipCaught,
            $"判定={r3.Result.VerdictText}，锁线={process.IsLocked}，当前步骤={process.CurrentStepText}，理由={r3.Result.JudgementReason}");

        // ---------------- T4：补做后复检通过 ----------------
        var r4 = Trigger(SimulatedAction.CorrectStep);
        Check("T4 补做被跳过的工序 → 复检通过、自动解锁、推进到第 2 道",
            r4.Result.Verdict == InspectionVerdict.Pass
            && r4.Advance == AdvanceOutcome.Advanced
            && !process.IsLocked
            && process.CurrentStep?.Seq == 2,
            $"判定={r4.Result.VerdictText}，流程={r4.Advance}，锁线={process.IsLocked}，当前步骤={process.CurrentStepText}");

        // ---------------- T5：漏装 / 未动作 ----------------
        var r5 = Trigger(SimulatedAction.NoAction);
        Check("T5 漏装（该做没做就触发）→ 判「进行中」，不推进不计数",
            r5.Result.Verdict == InspectionVerdict.None
            && r5.Advance == AdvanceOutcome.Ignored
            && !r5.CountsAsOk
            && !r5.CountsAsNg,
            $"判定={r5.Result.VerdictText}，流程={r5.Advance}，理由={r5.Result.JudgementReason}");

        // ---------------- T6：重复触发幂等 ----------------
        int okBefore = session.OkCount;
        var r6 = Trigger(SimulatedAction.RepeatTrigger);
        Check("T6 重复触发（画面不变）→ 幂等，不重复计数",
            r6.Advance == AdvanceOutcome.Ignored && session.OkCount == okBefore,
            $"流程={r6.Advance}，OK 计数 {okBefore} → {session.OkCount}");

        // ---------------- T7：人工放行 ----------------
        int overrideBefore = process.ManualOverrideCount;
        bool forced = process.ForceAdvance("自检：模拟管理员放行");
        Check("T7 人工放行 → 推进并被计数（人工干预率可追溯）",
            forced && process.ManualOverrideCount == overrideBefore + 1 && process.CurrentStep?.Seq == 3,
            $"放行结果={forced}，累计人工放行={process.ManualOverrideCount}，当前步骤={process.CurrentStepText}");

        // ---------------- T8：历史落盘 ----------------
        long stored = history.CountAsync().GetAwaiter().GetResult();
        Check("T8 OK/NG 结论逐条落历史；「进行中」不入库（可追溯且不灌水）",
            stored == _milestones && _milestones < _triggers,
            $"历史条数={stored}，有结论的触发={_milestones}，总触发={_triggers}");

        // ---------------- T9：复位 ----------------
        process.Reset();
        session.ResetForNewPiece();
        simulator.Reset(recipe);
        if (algorithm is SopSequenceAlgorithm seqReset2) seqReset2.ResetAll();

        Check("T9 复位 → 回到第 1 道工序、进度清零、算法记忆清空",
            process.CurrentStep?.Seq == 1
            && process.PassedCount == 0
            && session.LastProgress == 0
            && !process.IsLocked,
            $"当前步骤={process.CurrentStepText}，已完成={process.PassedCount}，进度={session.LastProgress}");

        // ---------------- T10：复位后能重新开始 ----------------
        var r10 = Trigger(SimulatedAction.CorrectStep);
        Check("T10 复位后重新开始一件 → 第 1 道工序正常通过（无凭空跳步误报）",
            r10.Result.Verdict == InspectionVerdict.Pass && r10.Advance == AdvanceOutcome.Advanced,
            $"判定={r10.Result.VerdictText}，流程={r10.Advance}，理由={r10.Result.JudgementReason}");

        // ================================================================
        // 过程层（v9 方案：手部关键点 + 规则引擎 + 实时拦截）
        // ================================================================
        RunProcessCases(Check, Note, root);

        // ---- 十三·13.5 三级界面权限（VM 级验证，不需要开界面）----
        var settingsProvider = new AppSettingsProvider(Path.Combine(root, "config", "appsettings.json"));
        var mockPlc = new MockPlcClient(settingsProvider.Current.Plc);
        var alarmDevice = AlarmFactory.Create(settingsProvider.Current.Alarm, mockPlc, settingsProvider.Current.PlcAddresses);
        var uiLogger = new FileLogger(Path.Combine(root, "logs"));
        var vm = new MainViewModel(registry, recipes, history, mockPlc, alarmDevice, uiLogger,
                                   new EventAggregator(), settingsProvider);

        // 角色切换现在走"要口令"的那条路（AttemptRoleChange），自检走的就是真实路径
        vm.AttemptRoleChange("操作员", null);
        Check("CCD-014 操作员界面权限：配方/设置页不可见、参数与人工放行被锁，只剩作业功能",
            !vm.CanSeeRecipePage && !vm.CanSeeSettingsPage && !vm.CanTuneRecipe && !vm.CanForceAdvance,
            vm.RolePermissionText);

        vm.AttemptRoleChange("技术员", "123456");
        Check("CCD-017 管理员（技术员）界面：可查历史、可复核，但改不了配方与系统设置",
            vm.CanSeeHistoryPage && !vm.CanSeeRecipePage && !vm.CanSeeSettingsPage,
            vm.RolePermissionText + "（用出厂默认口令 123456 登录成功）");

        vm.AttemptRoleChange("工程师", "888888");
        Check("CCD-018 工程师（运维）界面：配方 / 历史 / 设置全部开放",
            vm.CanSeeRecipePage && vm.CanSeeHistoryPage && vm.CanSeeSettingsPage,
            vm.RolePermissionText + "（用出厂默认口令 888888 登录成功）");

        // ================================================================
        // 权限（AC）：口令、锁定、改密、会话超时 —— 现场反馈"这个权限没有设计好，没有密码"
        // ================================================================
        var security = vm.Access!;

        // ---- AC-01 哈希不可逆、校验可靠 ----
        string hash1 = PasswordHasher.Hash("wf-2026");
        string hash2 = PasswordHasher.Hash("wf-2026");

        Check("AC-01 口令只存哈希：同一口令两次结果不同（有随机盐），正确口令能校验、错的不行、坏字符串不炸",
            hash1 != hash2
            && PasswordHasher.Verify("wf-2026", hash1)
            && !PasswordHasher.Verify("wf-2027", hash1)
            && !PasswordHasher.Verify("wf-2026", "这不是一个哈希")
            && !PasswordHasher.Verify("wf-2026", ""),
            $"两次哈希：{hash1[..18]}… / {hash2[..18]}…（都通过校验，错口令被拒）");

        // ---- AC-02 升级要口令，口令不对不升 ----
        vm.AttemptRoleChange("操作员", null);
        var wrong = vm.AttemptRoleChange("工程师", "000000");
        bool stayedOperator = vm.IsOperator;
        var right = vm.AttemptRoleChange("工程师", "888888");

        Check("AC-02 升级权限必须口令：错口令被拒绝且角色不变，对的口令才切到工程师",
            !wrong.Ok && stayedOperator && right.Ok && vm.IsEngineer,
            $"错口令：{wrong.Message}（角色还是 {(stayedOperator ? "操作员" : vm.CurrentUser)}）；" +
            $"对口令：{right.Message}");

        // ---- AC-03 降级不要口令（交还权限不该再拦） ----
        var downgrade = vm.AttemptRoleChange("操作员", null);
        Check("AC-03 降级不需要口令：工程师 → 操作员 直接生效（交还权限不该再拦一道）",
            downgrade.Ok && vm.IsOperator,
            downgrade.Message);

        // ---- AC-04 连续输错会临时锁定（但不会永久锁死） ----
        var lockMessages = new List<string>();
        for (int i = 0; i < 6; i++)
            lockMessages.Add(vm.AttemptRoleChange("技术员", "错的").Message);

        bool lockedMentioned = lockMessages.Any(m => m.Contains("锁定") || m.Contains("临时锁定"));
        var whileLocked = vm.AttemptRoleChange("技术员", "123456");   // 锁定期内，正确口令也进不去

        Check("AC-04 连续输错临时锁定：错够次数后提示锁定，且锁定期间即使口令正确也拒绝（防坐在这里猜）",
            lockedMentioned && !whileLocked.Ok,
            $"最后一次提示：{lockMessages[^1]}；锁定期间用正确口令的结果：{whileLocked.Message}");

        // 把锁定清掉，后面的用例要用
        security.Get("技术员").LockedUntil = null;

        // ---- AC-05 改口令的几条基本规矩 ----
        var badOld = security.ChangePassword("技术员", "不是原口令", "wf-1234", "wf-1234");
        var tooShort = security.ChangePassword("技术员", "123456", "12", "12");
        var mismatch = security.ChangePassword("技术员", "123456", "wf-1234", "wf-9999");
        var sameAsDefault = security.ChangePassword("技术员", "123456", "123456", "123456");
        var changed = security.ChangePassword("技术员", "123456", "wf-1234", "wf-1234");

        var oldRejected = security.SignIn("技术员", "123456");
        var newAccepted = security.SignIn("技术员", "wf-1234");

        Check("AC-05 改口令：原口令错 / 太短 / 两次不一致 / 与默认口令相同 —— 全部拒绝；改成功后旧口令失效、新口令生效",
            !badOld.Ok && !tooShort.Ok && !mismatch.Ok && !sameAsDefault.Ok
            && changed.Ok && !oldRejected.Ok && newAccepted.Ok,
            $"原口令错：{badOld.Message}；太短：{tooShort.Message}；不一致：{mismatch.Message}；" +
            $"与默认相同：{sameAsDefault.Message}；改成功后旧口令={oldRejected.Ok}、新口令={newAccepted.Ok}");

        // ---- AC-06 恢复默认口令是"后门"，必须锁死：需要工程师口令 ----
        bool resetWithWrong = vm.ResetRolePassword("技术员", "错的");
        bool resetWithRight = vm.ResetRolePassword("技术员", "888888");
        var backToDefault = security.SignIn("技术员", "123456");

        Check("AC-06 恢复默认口令必须验工程师口令：错了不改；对了才恢复（现场口令忘了的补救通道）",
            !resetWithWrong && resetWithRight && backToDefault.Ok,
            $"错口令被拒={!resetWithWrong}；恢复成功={resetWithRight}；恢复后默认口令可用={backToDefault.Ok}");

        // ---- AC-07 空闲超时自动退回操作员 ----
        vm.AttemptRoleChange("工程师", "888888");
        bool notYet = vm.EnforceSessionTimeout(TimeSpan.FromMinutes(1));
        bool timedOut = vm.EnforceSessionTimeout(TimeSpan.FromMinutes(99));

        Check("AC-07 会话超时：空闲没到点不降级；空闲超过设定时间自动退回操作员（工程师调完参数走了，权限不能留着）",
            !notYet && timedOut && vm.IsOperator,
            $"空闲 1 分钟={notYet}，空闲 99 分钟={timedOut}，现在角色={vm.CurrentUser}");

        // ---- AC-08 出厂默认口令要有提醒，改掉之后提醒消失 ----
        bool warnedWhileDefault = vm.HasDefaultPasswords;
        security.ChangePassword("工程师", "888888", "wf-eng", "wf-eng");
        security.ChangePassword("技术员", "123456", "wf-tec", "wf-tec");
        bool warnedAfterChange = vm.HasDefaultPasswords;

        Check("AC-08 默认口令提醒：只要还有角色在用出厂默认口令就提醒；全部改掉后提醒消失",
            warnedWhileDefault && !warnedAfterChange,
            $"改之前提醒={warnedWhileDefault}，全部改完之后提醒={warnedAfterChange}");

        // 把角色恢复到工程师再往下跑：后面的界面用例（建图 / 框位 / 步骤）都以"运维视角"验证。
        // 这一句同时也回归了"口令改完之后还能正常登录"。
        var backToWork = vm.AttemptRoleChange("工程师", "wf-eng");
        Check("AC-09 改完口令后仍能正常登录（用新口令切回工程师，供后续用例继续跑）",
            backToWork.Ok && vm.IsEngineer,
            backToWork.Message + $"；当前角色 {vm.CurrentUser}");

        // ---- 十八·建图三件事（UI-01~03）----
        //
        // 这三条是这次现场问题（"按住拖拽画不出框"）的回归测试。
        // 它们不动鼠标，直接调 ViewModel 上那三个公开方法 ——
        // 鼠标事件转发到 ViewModel 之后的那段逻辑，就是现场真正跑的那段。
        vm.ActiveRecipe = recipe;

        Check("UI-01 画面上的 ROI 框默认可见（旧版这个开关一直是关的，框永远看不见）",
            vm.ShowRoiBoxes && vm.RoiOverlays.Count == recipe.Rois.Count(r => r.Enabled),
            $"ShowRoiBoxes={vm.ShowRoiBoxes}，叠加框={vm.RoiOverlays.Count} 个 / 配方里 {recipe.Rois.Count} 个");

        vm.ToggleRoiEditCommand.Execute(null);

        int roisBefore = recipe.Rois.Count;
        vm.BeginRoiDraft(100, 100);
        vm.UpdateRoiDraft(300, 260);
        vm.CommitRoiDraft();
        Check("UI-02 拖拽画框：按下点 → 松开点就能成框（中间丢不丢移动事件都不影响）",
            recipe.Rois.Count == roisBefore + 1,
            $"配方里的框 {roisBefore} → {recipe.Rois.Count}，最新一个={recipe.Rois.Last().BoxText}");

        int roisBeforeClick = recipe.Rois.Count;
        vm.BeginRoiDraft(500, 400);
        vm.SetRoiDraftCentered(500, 400, 160);   // View 层在"只点了一下"时就是这么调的
        vm.CommitRoiDraft();
        Check("UI-03 单击画框：只点一下也能生成一个默认大小的框（不再有'点了没反应'）",
            recipe.Rois.Count == roisBeforeClick + 1,
            $"配方里的框 {roisBeforeClick} → {recipe.Rois.Count}，最新一个={recipe.Rois.Last().BoxText}");

        // ---- 十八·画面缩放与拖动（UI-04~05）----
        vm.ResetViewport();
        vm.SetViewportSize(960, 720);
        vm.ZoomAt(200, 150, 2.0);

        double scale = vm.ViewportScale;
        double anchorBefore = 200.0;                            // 复位后 pan=0、scale=1，锚点下的内容坐标就是 200
        double anchorAfter = (200 - vm.ViewportPanX) / scale;   // 缩放后同样的内容坐标

        Check("UI-04 滚轮缩放：以鼠标位置为中心，放大后鼠标指的那个点仍停在原处（不跑偏）",
            Math.Abs(scale - 2.0) < 1e-6 && Math.Abs(anchorBefore - anchorAfter) < 1e-6,
            $"倍数={scale:F2}，锚点内容坐标 {anchorBefore:F1} → {anchorAfter:F1}");

        vm.ZoomAt(200, 150, 1000.0);   // 疯狂放大，应被上限挡住
        bool capped = Math.Abs(vm.ViewportScale - MainViewModel.MaxViewportScale) < 1e-6;

        vm.SetViewportPan(-123, -45);    // 模拟"左键按住往左上拖画面"
        bool panned = Math.Abs(vm.ViewportPanX + 123) < 1e-6 && Math.Abs(vm.ViewportPanY + 45) < 1e-6;

        // 想拖到画面框外面 -> 必须被夹住（画面只能在框内动，不能留空白、也不能盖到别处）
        vm.SetViewportPan(9999, 9999);
        bool clampedToFrame = Math.Abs(vm.ViewportPanX) < 1e-6 && Math.Abs(vm.ViewportPanY) < 1e-6;

        vm.ResetViewport();
        Check("UI-05 拖动与复位：平移跟手、放大有上限、拖不出画面框（会被夹住）、复位回到 100% 居中",
            capped && panned && clampedToFrame && Math.Abs(vm.ViewportScale - 1.0) < 1e-6
            && Math.Abs(vm.ViewportPanX) < 1e-6 && Math.Abs(vm.ViewportPanY) < 1e-6,
            $"上限挡得住={capped}，平移生效={panned}，拖不出框={clampedToFrame}，" +
            $"复位后 {vm.ZoomText} / pan=({vm.ViewportPanX:F0},{vm.ViewportPanY:F0})");

        // ---- 十八·右键步骤小方框改名称/参数（UI-06）----
        vm.Sop.Load(sopDefinition);

        bool currentStepTextNotified = false;
        vm.Sop.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(SopProcess.CurrentStepText)) currentStepTextNotified = true;
        };

        var editStep = vm.Sop.Steps.First();
        var editModel = vm.CreateStepEdit(editStep);

        bool edited = false;
        if (editModel is not null)
        {
            editModel.Name = "自检改名·取件";
            editModel.Hint = "自检用的作业指引";
            editModel.TimeoutSec = 12;
            editModel.BlockNextOnFail = false;
            edited = vm.ApplyStepEdit(editModel);
        }

        var firstRoi = recipe.Rois.FirstOrDefault(r => r.Enabled);

        Check("UI-06 右键步骤小方框改名称/指引/超时：写回 SOP，同步画面上的框，并通知界面刷新",
            edited
            && editStep.Name == "自检改名·取件"
            && Math.Abs(editStep.TimeoutSec - 12) < 1e-6
            && !editStep.BlockNextOnFail
            && firstRoi is not null && firstRoi.Name == "自检改名·取件"
            && currentStepTextNotified,
            $"步骤名={editStep.Name}，超时={editStep.TimeoutSec:F0}s，锁线={editStep.BlockNextOnFail}，" +
            $"画面第 1 个框={firstRoi?.Name}，界面已收到刷新通知={currentStepTextNotified}");

        // ---- 十八·按框学习：教 OK（有东西）/ NG（东西被拿走）（UI-07）----
        using (var learnCamera = new MockCamera(320, 240, 99, "区域学习自检相机"))
        {
            learnCamera.ConnectAsync().GetAwaiter().GetResult();
            var learnScene = (ISceneCamera)learnCamera;

            var box = new RoiRegion { Name = "取件区", X = 0.10, Y = 0.10, Width = 0.40, Height = 0.40 };

            // 框里有零件 = OK
            learnScene.SetScene(new List<SceneBlob>
            {
                new() { X = 0.15, Y = 0.15, Width = 0.25, Height = 0.25 },
            });
            var filled = learnCamera.GrabOnceAsync(500).GetAwaiter().GetResult()!;

            // 零件被拿走、框里空着（东西在别处）= NG
            learnScene.SetScene(new List<SceneBlob>
            {
                new() { X = 0.60, Y = 0.60, Width = 0.25, Height = 0.25 },
            });
            var emptied = learnCamera.GrabOnceAsync(500).GetAwaiter().GetResult()!;

            var roiSamples = new List<RoiSample>
            {
                new() { RoiIndex = 1, RoiName = "取件区", IsOk = true,
                        Feature = FrameFeature.Extract(filled, box) },
                new() { RoiIndex = 1, RoiName = "取件区", IsOk = false,
                        Feature = FrameFeature.Extract(emptied, box) },
            };

            var roiLibrary = new RoiSampleLibrary();
            roiLibrary.Rebuild(roiSamples, 0.75);

            var learner = roiLibrary.Get(1);
            var judgeFilled = learner!.Recognize(FrameFeature.Extract(filled, box));
            var judgeEmpty = learner.Recognize(FrameFeature.Extract(emptied, box));
            bool untaughtBoxJudged = roiLibrary.CanJudge(2);

            Check("UI-07 按框学习：框里有东西教 OK、东西拿走教 NG —— 两种状态都判对；没教过的框不判",
                judgeFilled.IsOk == true && judgeEmpty.IsOk == false && !untaughtBoxJudged,
                $"有东西→{judgeFilled.IsOk?.ToString() ?? "无法判定"}({judgeFilled.Confidence:P0})，" +
                $"空框→{judgeEmpty.IsOk?.ToString() ?? "无法判定"}({judgeEmpty.Confidence:P0})，" +
                $"没教过的框会被判={untaughtBoxJudged}");
        }

        // ---- 十八·一格 = 一个框：格子数跟着框走，不再固定 6 格（UI-08）----
        int stepsBefore = vm.Sop.Steps.Count;

        // 连续画 5 个框，让框数超过工序数 -> 工序必须自己长出来
        for (int i = 0; i < 5; i++)
        {
            vm.BeginRoiDraft(600 + i * 20, 400);
            vm.UpdateRoiDraft(660 + i * 20, 460);
            vm.CommitRoiDraft();
        }

        int boxCount = recipe.Rois.Count(r => r.Enabled);

        Check("UI-08 一格 = 一个框：步骤条格子数 = 框数；框多于工序时工序自动补上（不再固定 6 格）",
            boxCount > stepsBefore
            && vm.RoiTargets.Count == boxCount
            && vm.Sop.Steps.Count == boxCount,
            $"画之前的工序数={stepsBefore}，画完框数={boxCount}，" +
            $"步骤条格子={vm.RoiTargets.Count}，工序数={vm.Sop.Steps.Count}");

        // ---- 删光框位 -> 工序同步清空（"框全删了还显示 2/9 步"的回归测试）（UI-09）----
        vm.ClearRoisCommand.Execute(null);

        Check("UI-09 删光框位后：工序同步清空，界面不再显示「x/9 步」（同一件事的两处显示必须一起变）",
            recipe.Rois.Count == 0
            && vm.RoiTargets.Count == 0
            && vm.Sop.Steps.Count == 0
            && vm.Sop.CurrentStep is null,
            $"框={recipe.Rois.Count}，框位格子={vm.RoiTargets.Count}，" +
            $"工序={vm.Sop.Steps.Count}，当前步骤={vm.Sop.CurrentStepText}");

        // ================================================================
        // AI 自主学习（自学习引擎：有把握才收 / NG 默认不自动学 / 人工纠错优先 /
        //                数量封顶只淘汰自学样本 / 随时可撤销）
        // ================================================================
        // 随机单位向量：193 维里两个随机方向的余弦≈0，天然互不相似，
        // 用来精确构造"很多条互不相同的样本"这种边界情况（真实画面很难凑齐）。
        float[] FakeFeature(int seed, int dim = FrameFeature.Dimension + 1)
        {
            var rnd = new Random(seed);
            var v = new float[dim];
            double norm = 0;
            for (int i = 0; i < dim; i++)
            {
                v[i] = (float)(rnd.NextDouble() * 2 - 1);
                norm += v[i] * (double)v[i];
            }

            norm = Math.Sqrt(norm);
            for (int i = 0; i < dim; i++) v[i] = (float)(v[i] / norm);
            return v;
        }

        SelfLearningObservation Obs(float[] feature, bool? judge, double confidence,
                                    bool stepCompleted = true, string source = SampleSource.Auto,
                                    int roi = 1)
            => new()
            {
                RecipeId = "自检配方",
                RoiIndex = roi,
                RoiName = "自检框",
                Feature = feature,
                Judge = judge,
                Confidence = confidence,
                StepCompleted = stepCompleted,
                Source = source,
            };

        var learnOptions = new SelfLearningOptions
        {
            Enabled = true,
            AutoCollectOk = true,
            AutoCollectNg = false,
            MinConfidence = 0.90,
            DedupSimilarity = 0.985,
            MaxPerClass = 40,
            RequireStepCompleted = true,
        };

        // ---- AI-01 三条护栏：拿不准 / 没做完 / 没把握，一条都不收 ----
        var guardPool = new List<RoiSample>();
        var lowConfidence = SelfLearningEngine.Apply(guardPool, Obs(FakeFeature(1), true, 0.72), learnOptions);
        var unknownJudge = SelfLearningEngine.Apply(guardPool, Obs(FakeFeature(2), null, 0.10), learnOptions);
        var notCompleted = SelfLearningEngine.Apply(guardPool,
            Obs(FakeFeature(3), true, 0.97, stepCompleted: false), learnOptions);

        Check("AI-01 自学护栏：把握不够 / 系统自己判不出 / 这一步还没正常完成 —— 三种情况一条样本都不收",
            lowConfidence.Decision == SelfLearningDecision.Rejected
            && unknownJudge.Decision == SelfLearningDecision.Rejected
            && notCompleted.Decision == SelfLearningDecision.Rejected
            && guardPool.Count == 0,
            $"低把握={lowConfidence.Decision}；无法判定={unknownJudge.Decision}；步骤未完成={notCompleted.Decision}；" +
            $"样本库={guardPool.Count} 条。首条理由：{lowConfidence.Reason}");

        // ---- AI-02 有把握就学；同一画面反复出现只强化不新增 ----
        var autoPool = new List<RoiSample>();
        var firstLearn = SelfLearningEngine.Apply(autoPool, Obs(FakeFeature(11), true, 0.96), learnOptions);
        var secondLearn = SelfLearningEngine.Apply(autoPool, Obs(FakeFeature(11), true, 0.97), learnOptions);

        Check("AI-02 有把握自动学：新画面收录 1 条；同一画面反复出现只强化（命中次数 +1），样本库不膨胀",
            firstLearn.Decision == SelfLearningDecision.Added
            && secondLearn.Decision == SelfLearningDecision.Reinforced
            && autoPool.Count == 1
            && autoPool[0].Hits == 2
            && autoPool[0].Source == SampleSource.Auto,
            $"第一次={firstLearn.Decision}，第二次={secondLearn.Decision}，" +
            $"库内 {autoPool.Count} 条（命中 {autoPool[0].Hits} 次，来源 {autoPool[0].SourceText}）");

        // ---- AI-03 NG 默认不自动学（误报学进去就洗不掉） ----
        var ngWhileOff = SelfLearningEngine.Apply(autoPool, Obs(FakeFeature(21), false, 0.98), learnOptions);
        learnOptions.AutoCollectNg = true;
        var ngWhileOn = SelfLearningEngine.Apply(autoPool, Obs(FakeFeature(21), false, 0.98), learnOptions);
        learnOptions.AutoCollectNg = false;

        Check("AI-03 NG 默认不自学（一次误报被学进去就再也洗不掉）；现场确认后打开开关才收录",
            ngWhileOff.Decision == SelfLearningDecision.Rejected
            && ngWhileOn.Decision == SelfLearningDecision.Added,
            $"开关关着={ngWhileOff.Decision}（{ngWhileOff.Reason}）；开关打开={ngWhileOn.Decision}");

        // ---- AI-04 数量封顶：只淘汰自学的，人工样本永远不动 ----
        var capOptions = learnOptions.Clone();
        capOptions.MaxPerClass = 3;

        var capPool = new List<RoiSample>
        {
            new() { RoiIndex = 1, RoiName = "自检框", IsOk = true,
                    Feature = FakeFeature(101), Source = SampleSource.Manual },
        };

        int evictedTimes = 0;
        for (int i = 0; i < 8; i++)
        {
            var outcome = SelfLearningEngine.Apply(capPool, Obs(FakeFeature(200 + i), true, 0.99), capOptions);
            if (outcome.Evicted is not null) evictedTimes++;
        }

        int okInCapPool = capPool.Count(s => s.IsOk);
        bool manualSurvived = capPool.Any(s => s.Source == SampleSource.Manual);

        Check("AI-04 样本封顶：每框每类到顶后自动淘汰「最没价值」的自学样本（被见到次数最少的先走）；人工教的样本一条都不动",
            okInCapPool <= 3 && manualSurvived && evictedTimes > 0,
            $"上限 3 条，连收 8 条后该类剩 {okInCapPool} 条（淘汰发生 {evictedTimes} 次），" +
            $"人工样本还在={manualSurvived}");

        // ---- AI-05 全是人工样本且已满 → 明确拒绝，绝不删人工样本 ----
        var manualOnlyOptions = learnOptions.Clone();
        manualOnlyOptions.MaxPerClass = 2;

        var manualOnlyPool = new List<RoiSample>
        {
            new() { RoiIndex = 1, IsOk = true, Feature = FakeFeature(301), Source = SampleSource.Manual },
            new() { RoiIndex = 1, IsOk = true, Feature = FakeFeature(302), Source = SampleSource.Manual },
        };

        var refused = SelfLearningEngine.Apply(manualOnlyPool, Obs(FakeFeature(303), true, 0.99), manualOnlyOptions);

        Check("AI-05 人工样本已满时：明确拒绝收录并提示人工清理，而不是偷偷删掉人教的样本",
            refused.Decision == SelfLearningDecision.Rejected
            && manualOnlyPool.Count == 2
            && manualOnlyPool.All(s => s.Source == SampleSource.Manual),
            $"结果={refused.Decision}，理由：{refused.Reason}；样本仍有 {manualOnlyPool.Count} 条");

        // ---- AI-06 撤销 / 固化 ----
        var rollbackPool = new List<RoiSample>
        {
            new() { RoiIndex = 1, RoiName = "自检框", IsOk = true,
                    Feature = FakeFeature(401), Source = SampleSource.Manual, Timestamp = DateTime.Now.AddMinutes(-9) },
        };

        // 故意让新样本的时间戳靠后（撤销取"最近一条自学的"）
        SelfLearningEngine.Apply(rollbackPool, Obs(FakeFeature(402), true, 0.99), learnOptions,
            DateTime.Now.AddMinutes(-2));
        SelfLearningEngine.Apply(rollbackPool, Obs(FakeFeature(403), true, 0.99), learnOptions, DateTime.Now);

        int beforeUndo = rollbackPool.Count;
        var undone = SelfLearningEngine.UndoLastAuto(rollbackPool);
        int frozen = SelfLearningEngine.FreezeAutoSamples(rollbackPool);
        var undoAfterFreeze = SelfLearningEngine.UndoLastAuto(rollbackPool);
        int autoLeft = rollbackPool.Count(s => s.Source == SampleSource.Auto);

        Check("AI-06 可回滚：撤销能撤掉最近学的一条；固化之后自学样本升级为人工样本、不再被撤销逻辑取走",
            beforeUndo == 3
            && undone is not null
            && rollbackPool.Count == 2
            && frozen == 1
            && undoAfterFreeze is null
            && autoLeft == 0,
            $"撤销前 {beforeUndo} 条 → 撤销 {undone?.Label} 后 {rollbackPool.Count} 条 → " +
            $"固化 {frozen} 条 → 再撤销={(undoAfterFreeze is null ? "无可撤销" : undoAfterFreeze.Label)}，" +
            $"剩余自学样本 {autoLeft} 条");

        // ---- AI-07 人工纠错优先级最高（关自学 / 低把握也照收） ----
        var correctedOptions = learnOptions.Clone();
        correctedOptions.Enabled = false;        // 自学总开关都关了

        var correctedPool = new List<RoiSample>();
        var corrected = SelfLearningEngine.Apply(correctedPool,
            Obs(FakeFeature(501), false, 0.10, stepCompleted: false, source: SampleSource.Corrected),
            correctedOptions);

        Check("AI-07 人工纠错优先：就算自学总开关关着、系统毫无把握，人说的那条照样入样本库且标记为纠错样本",
            corrected.Decision == SelfLearningDecision.Added
            && correctedPool.Count == 1
            && correctedPool[0].Source == SampleSource.Corrected
            && correctedPool[0].IsOk == false,
            $"结果={corrected.Decision}，样本={correctedPool.Count} 条，" +
            $"来源={correctedPool.FirstOrDefault()?.SourceText}，标签={correctedPool.FirstOrDefault()?.Label}");

        // ---- AI-08 真实链路：自学之后，原来的判定不会被带偏 ----
        using (var selfLearnCamera = new MockCamera(320, 240, 77, "自学自检相机"))
        {
            selfLearnCamera.ConnectAsync().GetAwaiter().GetResult();
            var selfScene = (ISceneCamera)selfLearnCamera;
            var selfBox = new RoiRegion { Name = "自学框", X = 0.10, Y = 0.10, Width = 0.40, Height = 0.40 };

            selfScene.SetScene(new List<SceneBlob>
            {
                new() { X = 0.15, Y = 0.15, Width = 0.25, Height = 0.25 },
            });
            var presentFrame = selfLearnCamera.GrabOnceAsync(500).GetAwaiter().GetResult()!;

            selfScene.SetScene(new List<SceneBlob>
            {
                new() { X = 0.60, Y = 0.60, Width = 0.25, Height = 0.25 },
            });
            var absentFrame = selfLearnCamera.GrabOnceAsync(500).GetAwaiter().GetResult()!;

            var presentFeature = FrameFeature.Extract(presentFrame, selfBox);
            var absentFeature = FrameFeature.Extract(absentFrame, selfBox);

            var liveSamples = new List<RoiSample>
            {
                new() { RoiIndex = 1, RoiName = "自学框", IsOk = true,
                        Feature = presentFeature, Source = SampleSource.Manual },
                new() { RoiIndex = 1, RoiName = "自学框", IsOk = false,
                        Feature = absentFeature, Source = SampleSource.Manual },
            };

            var liveLibrary = new RoiSampleLibrary();
            liveLibrary.Rebuild(liveSamples, 0.75);

            var beforeLearn = liveLibrary.Get(1)!.Recognize(presentFeature);

            var autoLearned = SelfLearningEngine.Apply(liveSamples,
                Obs(presentFeature, beforeLearn.IsOk, beforeLearn.Confidence), learnOptions);

            liveLibrary.Rebuild(liveSamples, 0.75);
            var afterLearner = liveLibrary.Get(1)!;
            var stillPresent = afterLearner.Recognize(presentFeature);
            var stillAbsent = afterLearner.Recognize(absentFeature);

            // 这里必须是 Reinforced 而不是 Added：同一帧的特征相似度是 100%，
            // 属于"同一个情况又见了一次"，按设计只强化、不新增。
            // （第一版这条用例写成了"应该新增"，结果自检直接报红 —— 是测试写错了，不是代码错了。）
            Check("AI-08 真实链路：同一画面自学只强化不新增（相似度 100%），之后「有东西=OK / 空框=NG」照旧判得对",
                autoLearned.Decision == SelfLearningDecision.Reinforced
                && liveSamples.Count == 2
                && stillPresent.IsOk == true
                && stillAbsent.IsOk == false,
                $"自学结果={autoLearned.Decision}（{autoLearned.Reason}）；" +
                $"自学后有东西→{stillPresent.IsOk?.ToString() ?? "无法判定"}({stillPresent.Confidence:P0})，" +
                $"空框→{stillAbsent.IsOk?.ToString() ?? "无法判定"}({stillAbsent.Confidence:P0})，" +
                $"样本 {liveSamples.Count} 条");

            // 换一帧"确实不一样"的画面（193 维随机方向，和现有样本几乎正交）：
            // 这次必须真的新增一条，而且新增之后原来的判定不能被动摇。
            var freshFeature = FakeFeature(777);
            var freshLearned = SelfLearningEngine.Apply(liveSamples,
                Obs(freshFeature, true, 0.99), learnOptions);

            liveLibrary.Rebuild(liveSamples, 0.75);
            var afterFresh = liveLibrary.Get(1)!;
            var stillPresentAfterFresh = afterFresh.Recognize(presentFeature);
            var stillAbsentAfterFresh = afterFresh.Recognize(absentFeature);

            Check("AI-09 自学新增一条真样本之后：样本库确实长了，而原来教对的判定（有东西=OK / 空框=NG）一点没被带偏",
                freshLearned.Decision == SelfLearningDecision.Added
                && liveSamples.Count == 3
                && stillPresentAfterFresh.IsOk == true
                && stillAbsentAfterFresh.IsOk == false,
                $"新增结果={freshLearned.Decision}，样本 {liveSamples.Count} 条；" +
                $"新增后有东西→{stillPresentAfterFresh.IsOk?.ToString() ?? "无法判定"}({stillPresentAfterFresh.Confidence:P0})，" +
                $"空框→{stillAbsentAfterFresh.IsOk?.ToString() ?? "无法判定"}({stillAbsentAfterFresh.Confidence:P0})");
        }

        // ---- AI-10 压力与耗时：这是 7×24 连跑最需要盯的一项 ----
        //
        // 造出"最坏情况"：9 个框、每框已经塞满 40 条 OK + 40 条 NG，
        // 然后连续送 300 次观察进来（一半是见过的画面、一半是全新画面）。
        // 要验证三件事：样本库不膨胀、不抛异常、每次观察的耗时在可接受范围内。
        var stressOptions = learnOptions.Clone();
        stressOptions.MaxPerClass = 40;

        var stressPool = new List<RoiSample>();
        for (int roi = 1; roi <= 9; roi++)
        {
            for (int i = 0; i < 40; i++)
            {
                stressPool.Add(new RoiSample
                {
                    RoiIndex = roi, RoiName = $"压测框{roi}", IsOk = true,
                    Feature = FakeFeature(10_000 + roi * 100 + i), Source = SampleSource.Auto,
                });
                stressPool.Add(new RoiSample
                {
                    RoiIndex = roi, RoiName = $"压测框{roi}", IsOk = false,
                    Feature = FakeFeature(20_000 + roi * 100 + i), Source = SampleSource.Auto,
                });
            }
        }

        int stressCeiling = 9 * 80;                 // 9 个框 × (40 OK + 40 NG)
        var stressWatch = System.Diagnostics.Stopwatch.StartNew();
        var stressRandom = new Random(20240913);
        int stressAdded = 0;

        for (int i = 0; i < 300; i++)
        {
            int roi = 1 + stressRandom.Next(9);

            // 一半复用已有画面（会走"强化"），一半是全新的（会触发"淘汰 + 新增"）
            float[] feature = i % 2 == 0
                ? stressPool.First(s => s.RoiIndex == roi && s.IsOk).Feature
                : FakeFeature(50_000 + i);

            var outcome = SelfLearningEngine.Apply(stressPool,
                Obs(feature, true, 0.99, roi: roi), stressOptions);

            if (outcome.Decision == SelfLearningDecision.Added) stressAdded++;
        }

        stressWatch.Stop();
        double stressPerCall = stressWatch.Elapsed.TotalMilliseconds / 300.0;

        Check("AI-10 压力：9 个框塞满 80 条样本后连收 300 次观察 —— 样本库不膨胀、不抛异常、单次耗时在 2ms 以内",
            stressPool.Count <= stressCeiling
            && stressAdded > 0
            && stressPerCall < 2.0,
            $"样本 {stressPool.Count} 条（上限 {stressCeiling}）· 新增 {stressAdded} 次 · " +
            $"300 次观察共 {stressWatch.Elapsed.TotalMilliseconds:F0}ms，平均 {stressPerCall:F2}ms/次");

        // 人工教的样本在最坏情况下也必须活着（压测池里全是自学样本，这里补一条人工的再压一轮）
        var keepManual = new List<RoiSample>
        {
            new() { RoiIndex = 1, RoiName = "人工锚点", IsOk = true,
                    Feature = FakeFeature(99_001), Source = SampleSource.Manual },
        };
        for (int i = 0; i < 120; i++)
        {
            SelfLearningEngine.Apply(keepManual, Obs(FakeFeature(99_100 + i), true, 0.99), stressOptions);
        }

        Check("AI-10b 压力下的人工样本保护：连收 120 条自学样本后，那条人工样本仍然在",
            keepManual.Count <= stressOptions.MaxPerClass
            && keepManual.Any(s => s.Source == SampleSource.Manual),
            $"最终 {keepManual.Count} 条，人工样本还在=" +
            $"{keepManual.Any(s => s.Source == SampleSource.Manual)}");

        // ---- 动作计时：落盘 / 读回 / 快慢判定 / 导出（UI-10）----
        string timingDir = Path.Combine(root, "timings");

        var first = new ActionTimingRecord
        {
            PieceId = "自检-0001", StepSeq = 1, StepName = "取件并放入工装",
            DurationMs = 5000, IsOk = true,
        };
        var second = new ActionTimingRecord
        {
            PieceId = "自检-0001", StepSeq = 1, StepName = "取件并放入工装",
            DurationMs = 7000, PreviousDurationMs = 5000, StandardMs = 5000, IsOk = true,
        };

        bool saved = ActionTimingStore.Append(first, timingDir) && ActionTimingStore.Append(second, timingDir);
        var timingLoaded = ActionTimingStore.Load(timingDir, DateTime.Today, DateTime.Today);
        var slowOne = timingLoaded.FirstOrDefault(r => r.DurationMs > 6000);

        string csvPath = Path.Combine(root, "timing-export.csv");
        ActionTimingStore.ExportCsv(timingLoaded, csvPath);
        bool csvOk = File.Exists(csvPath) && File.ReadAllLines(csvPath).Length >= 3;

        Check("UI-10 动作计时：记录能落盘/读回；「比上次慢了 2.0s」能算出来并标成偏慢；可导出 CSV",
            saved && timingLoaded.Count == 2
            && slowOne is not null
            && Math.Abs((slowOne.DeltaMs ?? 0) - 2000) < 1
            && slowOne.PaceStateKey == "ng"
            && slowOne.PaceText.Contains("慢了")
            && csvOk,
            $"写入={saved}，读回={timingLoaded.Count} 条，最慢那条：{slowOne?.PaceText}（DeltaMs={slowOne?.DeltaMs:F0}），导出行数≥3={csvOk}");

        // ---- 首页底部状态条（UI-11）----
        Check("UI-11 底部状态条：工单号/步骤进度/合规率/工位状态都能算出来（未接相机时明确显示「未连接」）",
            vm.WorkOrderText.StartsWith("WO-")
            && vm.StepProgressText == $"{vm.Sop.PassedCount} / {vm.Sop.TotalSteps}"
            && vm.StationStateText == "未连接",
            $"工单={vm.WorkOrderText}，型号={vm.ModelText}，SOP={vm.SopVersionText}，" +
            $"进度={vm.StepProgressText}，合规率={vm.ComplianceRateText}，" +
            $"节拍={vm.CycleTimeText}，工位={vm.StationStateText}");

        // ---- 录像当相机（UI-12）----
        // 造一段"有内容"的假视频不现实，这里验证的是"没有摄像头也能把录像当相机"这条通路：
        // 打不开的文件必须干净地失败（不抛异常、不留半截状态），这是现场最容易踩的坑
        // （选错文件、码流不认识、网络盘断开）。
        using (var missing = new VisionForge.Main.Imaging.VideoFileCamera(
                   Path.Combine(root, "not-exist.mp4")))
        {
            bool opened = missing.ConnectAsync().GetAwaiter().GetResult();

            Check("UI-12 录像当相机：打不开的文件要干净失败（不抛异常、不进入已连接状态）",
                !opened && !missing.IsConnected,
                $"打开结果={opened}，IsConnected={missing.IsConnected}，名称={missing.Info.DisplayName}");
        }

        // ---- 关键点叠加对象（UI-13）----
        bool posePointsReady = vm.PosePoints.Count == 21
                               && vm.PosePoints.All(p => !p.Visible)   // 没数据时不显示，不能骗人
                               && vm.ShowPosePoints;

        Check("UI-13 关键点叠加：固定 21 个点、没数据时不显示（不会在画面上留'上一帧的手'）",
            posePointsReady,
            $"点数={vm.PosePoints.Count}，全部隐藏={vm.PosePoints.All(p => !p.Visible)}，开关={vm.ShowPosePoints}");

        // ---- 框位右键删除（UI-14）----
        // 进标定模式才能画框（前面的用例已经开过，这里判一下避免又把它关掉）
        if (!vm.IsRoiEditMode) vm.ToggleRoiEditCommand.Execute(null);
        for (int i = 0; i < 3; i++)
        {
            vm.BeginRoiDraft(100 + i * 100, 100);
            vm.UpdateRoiDraft(200 + i * 100, 200);
            vm.CommitRoiDraft();
        }

        int boxesBeforeDelete = vm.RoiTargets.Count;
        int stepsBeforeDelete = vm.Sop.Steps.Count;

        vm.DeleteRoiByIndex(2);   // 删中间那个：后面的框序号要整体前移

        Check("UI-14 框位右键删除：框与工序一起少一个，剩下的框序号自动前移（不会跳号）",
            boxesBeforeDelete == 3
            && vm.RoiTargets.Count == boxesBeforeDelete - 1
            && vm.Sop.Steps.Count == stepsBeforeDelete - 1
            && vm.RoiTargets.Select(t => t.Index).SequenceEqual(new[] { 1, 2 }),
            $"删除前 {boxesBeforeDelete} 个框 / {stepsBeforeDelete} 道工序 → " +
            $"删除后 {vm.RoiTargets.Count} 个框 / {vm.Sop.Steps.Count} 道工序，" +
            $"剩余序号=[{string.Join(",", vm.RoiTargets.Select(t => t.Index))}]");

        // ---- 多工位汇总看板 + MES 契约（UI-15 / UI-16）----
        // 真起一个终端服务、真发一条工位上报、再从接口读回来 —— 这条链路是"几十个工位汇总"的核心，
        // 必须端到端验证，而不是只测一个函数。
        StationHub? hub = null;
        int hubPort = 0;
        for (int port = 18088; port <= 18092 && hub is null; port++)
        {
            try
            {
                var candidate = new StationHub("自检汇总终端");
                candidate.Start(port);
                hub = candidate;
                hubPort = port;
            }
            catch
            {
                // 端口被占用：换一个再试
            }
        }

        if (hub is null)
        {
            Check("UI-15 多工位汇总看板：终端服务能接收工位上报，并对外提供 JSON 接口与看板页面",
                false, "18088~18092 端口都被占用，跳过");
        }
        else
        {
            using (hub)
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var report = new StationReport
                {
                    StationCode = "ST-99",
                    StationName = "自检工位",
                    LineName = "Line-T",
                    StateText = "运行中",
                    StateKey = "ok",
                    CurrentStepName = "安装电机",
                    StepDone = 2,
                    StepTotal = 3,
                    OkCount = 12,
                    NgCount = 1,
                    CompletedPieces = 4,
                    PassRate = 92.3,
                    LastCycleSec = 31.5,
                };

                string body = System.Text.Json.JsonSerializer.Serialize(report);
                var post = http.PostAsync($"http://127.0.0.1:{hubPort}/api/report",
                    new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"))
                    .GetAwaiter().GetResult();

                string api = http.GetStringAsync($"http://127.0.0.1:{hubPort}/api/stations").GetAwaiter().GetResult();
                string page = http.GetStringAsync($"http://127.0.0.1:{hubPort}/").GetAwaiter().GetResult();

                Check("UI-15 多工位汇总看板：终端服务能接收工位上报，并对外提供 JSON 接口与看板页面",
                    post.IsSuccessStatusCode
                    && api.Contains("ST-99")
                    && api.Contains("\"online\":1")
                    && page.Contains("AI-SOP 工位实时看板"),
                    $"上报 HTTP={(int)post.StatusCode}；接口含工位={api.Contains("ST-99")}；" +
                    $"看板页面 {page.Length}B（端口 {hubPort}）");

                // MES 契约：件数据能翻译成字段固定、时间统一的上传体
                var mesPayload = new MesUploadPayload
                {
                    StationCode = "ST-99",
                    PieceId = "自检件-0001",
                    StartedAt = DateTime.Now.AddSeconds(-30).ToString("yyyy-MM-dd HH:mm:ss"),
                    FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    CycleSec = 30,
                    Verdict = "NG",
                    ViolationCode = (int)ViolationCode.StepSkipped,
                    JudgementReason = "跳步：跳过第 2 步",
                    Steps = new List<MesStepPayload>
                    {
                        new() { Seq = 1, Name = "取件", DurationSec = 4.2, IsOk = true },
                        new() { Seq = 2, Name = "装电机", DurationSec = 6.8, IsOk = false },
                    },
                };

                string mesJson = System.Text.Json.JsonSerializer.Serialize(mesPayload);
                Check("UI-16 MES 契约：上传体字段固定（SchemaVersion / 工位 / 件号 / OK-NG / 违规码 / 工序明细）",
                    mesJson.Contains("\"SchemaVersion\":\"1.0\"")
                    && mesJson.Contains("\"StationCode\":\"ST-99\"")
                    && mesJson.Contains("\"Verdict\":\"NG\"")
                    && mesJson.Contains("\"ViolationCode\":0")
                    && mesJson.Contains("\"Steps\""),
                    $"上传体 {mesJson.Length}B：" + mesJson[..Math.Min(120, mesJson.Length)] + "…");

                // ---- 模板池与一键下发（UI-20）：上传 → 下发 → 工位拉取 → 回执 ----
                var deployPackage = new DeployPackage
                {
                    Name = "自检模板 · 主机装配",
                    SourceStation = "ST-99",
                    ProcessRule = new ProcessRuleConfig { Kms = 300, TimeWindowMs = 8000 },
                };
                deployPackage.TimingStandards.Add(new StepTimeStandard { StepSeq = 1, StepName = "取件", StandardSec = 5 });

                var uploadResp = http.PostAsync($"http://127.0.0.1:{hubPort}/api/template/upload",
                    new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(deployPackage),
                        Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                string uploadBody = uploadResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                string poolId = "";
                using (var doc = System.Text.Json.JsonDocument.Parse(uploadBody))
                    if (doc.RootElement.TryGetProperty("poolId", out var idEl)) poolId = idEl.GetString() ?? "";

                // 终端一键下发
                var publishResp = http.PostAsync($"http://127.0.0.1:{hubPort}/api/template/publish",
                    new System.Net.Http.StringContent($"{{\"poolId\":\"{poolId}\"}}", Encoding.UTF8, "application/json"))
                    .GetAwaiter().GetResult();

                // 工位拉取：第一次应该拿到包，回执之后再拉应该 unchanged（不能每次轮询都重放）
                string firstPull = http.GetStringAsync(
                    $"http://127.0.0.1:{hubPort}/api/template/assigned?station=ST-99&applied=0").GetAwaiter().GetResult();
                http.PostAsync($"http://127.0.0.1:{hubPort}/api/template/applied",
                    new System.Net.Http.StringContent("{\"station\":\"ST-99\",\"version\":1}", Encoding.UTF8, "application/json"))
                    .GetAwaiter().GetResult();
                string secondPull = http.GetStringAsync(
                    $"http://127.0.0.1:{hubPort}/api/template/assigned?station=ST-99&applied=1").GetAwaiter().GetResult();
                string poolJson = http.GetStringAsync($"http://127.0.0.1:{hubPort}/api/template/pool").GetAwaiter().GetResult();
                string statusJson = http.GetStringAsync($"http://127.0.0.1:{hubPort}/api/template/status").GetAwaiter().GetResult();

                bool gotPackage = firstPull.Contains("\"unchanged\":false")
                                  && firstPull.Contains("自检模板")
                                  // 接口统一用 camelCase（工位端按大小写不敏感解析），
                                  // 所以这里必须查 timingStandards 而不是 PascalCase ——
                                  // 原来查 PascalCase，是这条用例假失败的原因。
                                  && firstPull.Contains("timingStandards");
                bool noRepeat = secondPull.Contains("\"unchanged\":true");
                bool poolOk = poolJson.Contains("自检模板") && poolJson.Contains("ST-99");
                bool appliedTracked = statusJson.Contains("\"appliedCount\":1");

                Check("UI-20 模板池与一键下发：工位上传模板 → 终端一键下发 → 工位拉取到包 → 回执后不重复下发",
                    !string.IsNullOrEmpty(poolId)
                    && publishResp.IsSuccessStatusCode
                    && gotPackage && noRepeat && poolOk && appliedTracked,
                    $"模板池Id={poolId[..Math.Min(8, poolId.Length)]}，下发 HTTP={(int)publishResp.StatusCode}，" +
                    $"拉取到包={gotPackage}，已应用后不再下发={noRepeat}，池列表={poolOk}，回执统计={appliedTracked}");
            }
        }

        // ---- 连续试跑 30 件（UI-17）：稳定性冒烟 ----
        var soakProcess = new SopProcess();
        soakProcess.Load(sopDefinition);
        var soakSession = new MonitoringSession(soakProcess);

        for (int piece = 0; piece < 30; piece++)
        {
            for (int step = 1; step <= 6; step++)
            {
                soakSession.Process(new InspectionResult
                {
                    RecipeId = recipe.Id,
                    RecipeName = recipe.Name,
                    Verdict = InspectionVerdict.Pass,
                    ProgressSteps = step,
                    JudgementReason = "连续试跑",
                });
            }

            // 与生产代码一致：整件完成后复位，开始下一件
            soakProcess.Reset();
            soakSession.ResetForNewPiece();
        }

        Check("UI-17 连续试跑 30 件：每件 6 道工序按序完成 → 完成件数 / 通过数 / 流程状态全部自洽",
            soakSession.CompletedPieces == 30
            && soakSession.OkCount == 180
            && soakSession.NgCount == 0
            && !soakProcess.IsLocked,
            $"完成 {soakSession.CompletedPieces} 件，OK {soakSession.OkCount} 次，" +
            $"NG {soakSession.NgCount} 次，锁线={soakProcess.IsLocked}");

        // ---- MES 断网续传（UI-18）：失败一条都不能丢，恢复后全部补发 ----
        string queueDir = Path.Combine(root, "mes-queue-test");
        var queue = new MesUploadQueue(queueDir);

        for (int i = 0; i < 20; i++)
        {
            queue.Enqueue(new MesUploadPayload
            {
                StationCode = "ST-Q", PieceId = $"Q-{i:D3}", Verdict = "OK",
                FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }

        int queued = queue.PendingCount;
        int sentWhileDown = queue.FlushAsync(new AlwaysFailMesClient()).GetAwaiter().GetResult();
        int keptAfterFail = queue.PendingCount;
        int drained = queue.FlushAsync(new AlwaysOkMesClient()).GetAwaiter().GetResult();
        int leftAfterOk = queue.PendingCount;

        Check("UI-18 MES 断网续传：上传失败时数据一条不丢，网络恢复后全部自动补发",
            queued == 20 && sentWhileDown == 0 && keptAfterFail == 20 && drained == 20 && leftAfterOk == 0,
            $"入队 {queued} → 断网期间发出 {sentWhileDown}（剩余 {keptAfterFail}）→ " +
            $"恢复后补发 {drained}（剩余 {leftAfterOk}）");

        // ---- 规则引擎违规列表有上限（UI-19）：7×24 连跑不能无限涨内存 ----
        var capConfig = new ProcessRuleConfig
        {
            Fps = 100, Kms = 100, TimeWindowMs = 500,
            MinConfidence = 0.5, MaxUnreliableMs = 300, OrderEnforced = true,
        };
        capConfig.Targets.Add(new ProcessTarget { Id = "P1", Name = "甲", X = 0.10, Y = 0.10, Width = 0.20, Height = 0.20 });
        capConfig.Targets.Add(new ProcessTarget { Id = "P2", Name = "乙", X = 0.40, Y = 0.10, Width = 0.20, Height = 0.20 });
        capConfig.Steps.Add(new ProcessStepDefinition { Seq = 1, Name = "第一步", Targets = new List<string> { "P1" } });
        capConfig.Steps.Add(new ProcessStepDefinition { Seq = 2, Name = "第二步", Targets = new List<string> { "P2" } });

        var capEngine = new HandActionRuleEngine(capConfig);
        for (int round = 0; round < 600; round++)
        {
            capEngine.Reset();
            var skipScript = new List<PoseSegment>
            {
                new() { TargetId = "P2", DurationMs = 200, Note = "跳过第一步" },
            };

            foreach (var frame in new SimulatedPoseSource(capConfig, skipScript).Frames)
            {
                if (capEngine.Feed(frame).Verdict == ProcessVerdict.Violation) break;
            }
        }

        Check("UI-19 规则引擎违规列表有上限：反复造违规也不会无限涨（7×24 连跑不涨内存）",
            capEngine.AllViolations.Count is > 0 and <= 500,
            $"造了 600 轮违规，内存里保留 {capEngine.AllViolations.Count} 条（上限 500，完整记录在日志与历史库）");

        return Finish(root, reportPath);
    }

    // ==================================================================
    /// <summary>
    /// 过程层自检：用脚本化的手部轨迹喂规则引擎，验证六类违规与两条防误拦补丁。
    ///
    /// <para>测试用的配置刻意取小值（K=100ms、100fps、时间窗 2s），
    /// 这样整段轨迹只有几十帧，跑得飞快；判定逻辑与现场一模一样，只是数值缩放。</para>
    /// </summary>
    private void RunProcessCases(Action<string, bool, string> check, Action<string, string> note, string root)
    {
        // ---- 基准配置：3 个料盒、3 道工序、严格顺序 ----
        ProcessRuleConfig NewConfig(bool orderEnforced = true, int expectCountP1 = 0)
        {
            var config = new ProcessRuleConfig
            {
                StationName = "自检工位",
                Fps = 100,
                Kms = 100,
                TimeWindowMs = 2000,
                MinConfidence = 0.5,
                MaxUnreliableMs = 300,
                OrderEnforced = orderEnforced,
            };
            config.Targets.Add(new ProcessTarget { Id = "P1", Name = "弹簧盒", X = 0.10, Y = 0.10, Width = 0.20, Height = 0.20, ExpectedCount = expectCountP1 });
            config.Targets.Add(new ProcessTarget { Id = "P2", Name = "产品壳", X = 0.40, Y = 0.10, Width = 0.20, Height = 0.20 });
            config.Targets.Add(new ProcessTarget { Id = "P3", Name = "密封圈", X = 0.70, Y = 0.10, Width = 0.20, Height = 0.20 });
            config.Steps.Add(new ProcessStepDefinition { Seq = 1, Name = "取弹簧", Targets = new List<string> { "P1" } });
            config.Steps.Add(new ProcessStepDefinition { Seq = 2, Name = "装产品壳", Targets = new List<string> { "P2" } });
            config.Steps.Add(new ProcessStepDefinition { Seq = 3, Name = "装密封圈", Targets = new List<string> { "P3" } });
            return config;
        }

        // 跑完一整段轨迹，返回（违规集合、最后一帧结论、是否整件完成）
        (List<ProcessViolation> Violations, ProcessEvaluation Last, bool Completed) RunProcess(
            ProcessRuleConfig config, List<PoseSegment> script)
        {
            var engine = new HandActionRuleEngine(config);
            var source = new SimulatedPoseSource(config, script);

            ProcessEvaluation last = new();
            bool completed = false;

            foreach (var frame in source.Frames)
            {
                last = engine.Feed(frame);
                if (last.Verdict == ProcessVerdict.AllCompleted) { completed = true; break; }
            }

            return (engine.AllViolations.ToList(), last, completed);
        }

        // ---- S1：规范作业 → 全部合规 ----
        var cfg1 = NewConfig();
        var s1 = RunProcess(cfg1, SimulatedPoseSource.BuildCorrectScript(cfg1));
        check("S1 规范作业（三步按序到位）→ 整件合规、无违规记录",
            s1.Completed && s1.Violations.Count == 0,
            $"完成={s1.Completed}，违规={s1.Violations.Count} 条，末帧={s1.Last.Message}");

        // ---- S2：跳步 → 错序 + 跳步 ----
        var cfg2 = NewConfig();
        var s2 = RunProcess(cfg2, SimulatedPoseSource.BuildSkipScript(cfg2, 1));
        bool hasOutOfOrder = s2.Violations.Any(v => v.Code == ViolationCode.StepOutOfOrder);
        bool hasSkipped = s2.Violations.Any(v => v.Code == ViolationCode.StepSkipped);
        check("S2 跳过第 1 道直接做第 2 道 → STEP_OUT_OF_ORDER + STEP_SKIPPED，且不推进",
            hasOutOfOrder && hasSkipped && !s2.Completed,
            $"错序={hasOutOfOrder}，跳步={hasSkipped}，完成={s2.Completed}，理由={s2.Violations.FirstOrDefault()?.Message}");

        // ---- S3：同一条轨迹，关掉顺序开关 → 不再报错序 ----
        var cfg3 = NewConfig(orderEnforced: false);
        var s3 = RunProcess(cfg3, SimulatedPoseSource.BuildSkipScript(cfg3, 1));
        bool anyOutOfOrder3 = s3.Violations.Any(v => v.Code == ViolationCode.StepOutOfOrder);
        check("S3 关掉「步骤间顺序」开关 → 同样的轨迹不再报错序（开关真的生效）",
            !anyOutOfOrder3,
            $"错序违规={anyOutOfOrder3}，违规明细=[{string.Join(" / ", s3.Violations.Select(v => v.CodeText))}]");

        // ---- S4：快掠（停留不够）→ 目标未完成 ----
        var cfg4 = NewConfig();
        var script4 = SimulatedPoseSource.BuildTooFastScript(cfg4);
        script4.Add(new PoseSegment { X = 0.5, Y = 0.95, DurationMs = 2400, Note = "手离开，等时间窗耗尽" });
        var s4 = RunProcess(cfg4, script4);
        check("S4 手快掠（停留 30ms < K=100ms）→ STEP_NOT_DONE（动作不达标，不是系统太严）",
            s4.Violations.Any(v => v.Code == ViolationCode.StepNotDone),
            $"违规明细=[{string.Join(" / ", s4.Violations.Select(v => v.Message))}]");

        // ---- S5：遮挡 → 报警但不判违规，且遮挡后接着累加 ----
        var cfg5 = NewConfig();
        var s5 = RunProcess(cfg5, SimulatedPoseSource.BuildOcclusionScript(cfg5, blockedMs: 500));
        bool unreliable = s5.Violations.Any(v => v.Code == ViolationCode.Unreliable);
        bool noFalseViolation = !s5.Violations.Any(v =>
            v.Code is ViolationCode.StepOutOfOrder or ViolationCode.StepSkipped or ViolationCode.StepNotDone);
        check("S5 手被挡住 500ms → 报 UNRELIABLE 报警，但不算违规；遮挡后继续累计并完成",
            unreliable && noFalseViolation && s5.Completed,
            $"报警={unreliable}，无误判违规={noFalseViolation}，整件完成={s5.Completed}，" +
            $"违规明细=[{string.Join(" / ", s5.Violations.Select(v => v.CodeText))}]");

        // ---- S6：对象状态轨数量不足 → COUNT_MISMATCH ----
        var cfg6 = NewConfig(expectCountP1: 2);
        var script6 = new List<PoseSegment>
        {
            new() { TargetId = "P1", DurationMs = 150, TargetCounts = new Dictionary<string, int> { ["P1"] = 1 }, Note = "只放了 1 个（要求 2 个）" },
        };
        var s6 = RunProcess(cfg6, script6);
        check("S6 对象状态轨：期望 2 个实际 1 个 → COUNT_MISMATCH",
            s6.Violations.Any(v => v.Code == ViolationCode.CountMismatch),
            $"违规明细=[{string.Join(" / ", s6.Violations.Select(v => v.Message))}]");

        // ---- S7：规则配置校验 ----
        var badConfig = NewConfig();
        badConfig.Steps.Add(new ProcessStepDefinition { Seq = 4, Name = "引用了不存在的目标", Targets = new List<string> { "P9" } });
        badConfig.Targets[0].X = 1.5;   // 越界
        var errors = badConfig.Validate();
        check("S7 规则配置校验：目标越界 + 引用不存在目标 → 报出明确错误（对应 CONFIG_INVALID）",
            errors.Count >= 2,
            $"校验错误 {errors.Count} 条：{string.Join("；", errors)}");

        // ---- S8：YAML 导出 → 重新导入 → 字段一致 ----
        var cfg8 = NewConfig();
        bool yamlOk;
        string yamlDetail;
        try
        {
            string yaml = ProcessConfigStore.ToYaml(cfg8);
            var back = ProcessConfigStore.FromYaml(yaml);

            yamlOk = back.Targets.Count == cfg8.Targets.Count
                     && back.Steps.Count == cfg8.Steps.Count
                     && Math.Abs(back.Kms - cfg8.Kms) < 0.001
                     && Math.Abs(back.Fps - cfg8.Fps) < 0.001
                     && back.OrderEnforced == cfg8.OrderEnforced
                     && back.Steps[1].Targets.SequenceEqual(cfg8.Steps[1].Targets)
                     && Math.Abs(back.Targets[2].X - cfg8.Targets[2].X) < 0.0001;
            yamlDetail = $"目标 {back.Targets.Count} 个、工序 {back.Steps.Count} 道、K={back.Kms}ms、顺序开关={back.OrderEnforced}";
        }
        catch (Exception ex)
        {
            yamlOk = false;
            yamlDetail = "YAML 往返失败：" + ex.Message;
        }
        check("S8 YAML 规则导出后再导入 → 目标/工序/阈值完全一致", yamlOk, yamlDetail);

        // ---- S9：坏 YAML 必须报错，而不是静默变成"什么都不做" ----
        bool badYamlRejected;
        string badYamlDetail;
        try
        {
            ProcessConfigStore.FromYaml("station:\n  name: 坏配置\n  fps: 0\ntargets: []\nsteps: []\n");
            badYamlRejected = false;
            badYamlDetail = "竟然通过了 —— 配置校验没拦住";
        }
        catch (Exception ex)
        {
            badYamlRejected = true;
            badYamlDetail = "已按预期拒绝：" + ex.Message;
        }
        check("S9 残缺 YAML（无目标、fps=0）→ 明确报错（对应 CONFIG_INVALID，而不是运行期莫名不动作）",
            badYamlRejected, badYamlDetail);

        // ================================================================
        // 动作教示识别（现场用法：不画框，直接教 OK / NG，之后自动识别）
        // ================================================================
        // 造确定画面：4 个位置，指定哪几个亮着
        CameraFrame MakeScene(params bool[] lit)
        {
            const int w = 128, h = 96;
            var data = new byte[w * h];
            Array.Fill(data, (byte)35);

            int cell = w / 4;
            for (int i = 0; i < lit.Length && i < 4; i++)
            {
                if (!lit[i]) continue;
                int x0 = i * cell;
                for (int y = 20; y < 70; y++)
                    for (int x = x0; x < x0 + cell - 6; x++)
                        data[y * w + x] = 220;
            }

            return new CameraFrame(w, h, w, PixelFormat.Mono8, data);
        }

        var okScene = MakeScene(true, false, false, false);
        var ngScene = MakeScene(false, true, false, false);
        var unknownScene = MakeScene(false, false, true, false);

        var labeled = new List<ActionSample>
        {
            new() { IsOk = true, Feature = FrameFeature.Extract(okScene) },
            new() { IsOk = false, Feature = FrameFeature.Extract(ngScene) },
        };
        var recognize = new ActionSampleClassifier(labeled);

        var r1 = recognize.Recognize(FrameFeature.Extract(okScene));
        check("U1 教示后：同样的标准动作 → 判 OK 且相似度高",
            r1.IsOk == true && r1.Confidence > 0.9,
            $"判定={r1.IsOk}，相似度={r1.Confidence:P1}，{r1.Message}");

        var r2 = recognize.Recognize(FrameFeature.Extract(ngScene));
        check("U2 教示后：教过的违规动作 → 判 NG",
            r2.IsOk == false && r2.Confidence > 0.9,
            $"判定={r2.IsOk}，相似度={r2.Confidence:P1}，{r2.Message}");

        var r3 = recognize.Recognize(FrameFeature.Extract(unknownScene));
        check("U3 没见过的新动作 → 判「无法判定」（不硬猜、不误拦），提示补样本",
            r3.IsOk is null,
            $"判定={r3.IsOk?.ToString() ?? "无法判定"}，最高相似度={r3.Confidence:P1}，{r3.Message}");

        // ---- V1：批量仿真（留一法）能自证"教出来的样本有没有区分度" ----
        var validationSamples = new List<ActionSample>();
        for (int i = 0; i < 3; i++)
        {
            validationSamples.Add(new ActionSample { IsOk = true, Feature = FrameFeature.Extract(okScene) });
            validationSamples.Add(new ActionSample { IsOk = false, Feature = FrameFeature.Extract(ngScene) });
        }

        var report = ActionSampleValidator.LeaveOneOut(validationSamples, 0.75);
        check("V1 批量仿真（留一法）：教示样本自身可区分 → 准确率 100%、漏检/误检 0",
            report.Total == 6
            && Math.Abs(report.Accuracy - 1.0) < 0.0001
            && report.Missed == 0
            && report.FalseAlarm == 0,
            report.Summary);

        // ================================================================
        // 十三、标准化测试用例（编号对齐方案 CCD-001 ~ CCD-026）
        // ================================================================

        // ---- CCD-001 相机参数采集有效性 ----
        var paramCamera = new MockCamera(128, 96, 7, "参数测试相机");
        paramCamera.ConnectAsync().GetAwaiter().GetResult();
        paramCamera.ApplyParametersAsync(new Dictionary<string, double>
        {
            ["ExposureTime"] = 8000,
            ["Gain"] = 6,
            ["FrameRate"] = 15,
        }).GetAwaiter().GetResult();

        check("CCD-001 相机参数采集有效性：曝光/增益/帧率下发后，相机侧确实收到",
            paramCamera.LastAppliedParameters.Count == 3
            && Math.Abs(paramCamera.LastAppliedParameters["ExposureTime"] - 8000) < 0.001
            && Math.Abs(paramCamera.LastAppliedParameters["Gain"] - 6) < 0.001,
            "相机侧参数：" + string.Join("，", paramCamera.LastAppliedParameters.Select(kv => kv.Key + "=" + kv.Value)));

        // ---- CCD-002 复杂工况成像（噪点下仍可识别）----
        CameraFrame AddNoise(CameraFrame source, int count, int seed)
        {
            var data = (byte[])source.Data.Clone();
            var random = new Random(seed);
            for (int i = 0; i < count; i++)
                data[random.Next(data.Length)] = (byte)(random.Next(2) == 0 ? 0 : 255);

            return new CameraFrame(source.Width, source.Height, source.Stride, source.Format, data);
        }

        var noisyRecognition = recognize.Recognize(FrameFeature.Extract(AddNoise(okScene, 200, 11)));
        check("CCD-002 复杂工况成像：叠加 200 个椒盐噪点后，标准动作仍判 OK",
            noisyRecognition.IsOk == true,
            $"判定={noisyRecognition.IsOk?.ToString() ?? "无法判定"}，相似度={noisyRecognition.Confidence:P1}");

        // ---- CCD-003 多路相机同步采集 ----
        var camA = new MockCamera(64, 48, 1, "A 路");
        var camB = new MockCamera(64, 48, 2, "B 路");
        camA.ConnectAsync().GetAwaiter().GetResult();
        camB.ConnectAsync().GetAwaiter().GetResult();

        var a1 = camA.GrabOnceAsync().GetAwaiter().GetResult();
        var b1 = camB.GrabOnceAsync().GetAwaiter().GetResult();
        var a2 = camA.GrabOnceAsync().GetAwaiter().GetResult();
        var b2 = camB.GrabOnceAsync().GetAwaiter().GetResult();

        check("CCD-003 双路相机同步采集：两路各自出图、帧号独立递增、互不干扰",
            a1 is not null && b1 is not null && a2 is not null && b2 is not null
            && a2.FrameNumber > a1.FrameNumber && b2.FrameNumber > b1.FrameNumber,
            $"A 路帧号 {a1?.FrameNumber}→{a2?.FrameNumber}，B 路帧号 {b1?.FrameNumber}→{b2?.FrameNumber}");

        // ---- CCD-004 预处理稳定性（亮度漂移）----
        CameraFrame Brighten(CameraFrame source, int delta)
        {
            var data = (byte[])source.Data.Clone();
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)Math.Clamp(data[i] + delta, 0, 255);

            return new CameraFrame(source.Width, source.Height, source.Stride, source.Format, data);
        }

        double brightnessSimilarity = FrameFeature.Similarity(
            FrameFeature.Extract(okScene), FrameFeature.Extract(Brighten(okScene, 30)));
        check("CCD-004 图像预处理稳定性：整体亮度 +30 后特征仍高度一致（去均值生效）",
            brightnessSimilarity > 0.95,
            $"亮度漂移前后特征相似度 {brightnessSimilarity:P1}");

        // ---- CCD-007 缺陷/污点识别（不能漏检成良品）----
        var defectScene = MakeScene(true, false, true, false);   // 良品场景上多出一处污点
        var defectRecognition = recognize.Recognize(FrameFeature.Extract(defectScene));
        check("CCD-007 缺陷样本识别：良品场景多出一处污点 → 不会被判成 OK（不漏检）",
            defectRecognition.IsOk != true,
            $"判定={defectRecognition.IsOk?.ToString() ?? "无法判定"}，相似度={defectRecognition.Confidence:P1}");

        // ---- CCD-009 统计稳定性 ----
        int stableCount = 0;
        for (int i = 0; i < 10; i++)
            if (recognize.Recognize(FrameFeature.Extract(okScene)).IsOk == true) stableCount++;

        check("CCD-009 计数/统计稳定性：同一画面连续判定 10 次，结论不跳动",
            stableCount == 10,
            $"10 次判定中判 OK 的次数：{stableCount}");

        // ---- CCD-012 离线批量仿真（256 组）----
        var largeBatch = new List<ActionSample>();
        for (int i = 0; i < 128; i++)
        {
            largeBatch.Add(new ActionSample { IsOk = true, Feature = FrameFeature.Extract(AddNoise(okScene, 60, 1000 + i)) });
            largeBatch.Add(new ActionSample { IsOk = false, Feature = FrameFeature.Extract(AddNoise(ngScene, 60, 2000 + i)) });
        }

        var batchWatch = System.Diagnostics.Stopwatch.StartNew();
        var batchReport = ActionSampleValidator.LeaveOneOut(largeBatch, 0.75);
        batchWatch.Stop();

        check("CCD-012 离线批量仿真：256 组样本一次跑完，统计准确、不卡死",
            batchReport.Total == 256 && batchReport.Accuracy >= 0.9,
            batchReport.Summary + $"，总耗时 {batchWatch.ElapsedMilliseconds}ms");

        // ---- CCD-013 历史回放复现（落盘 → 重新载入 → 特征一致）----
        bool replayOk;
        string replayDetail;
        try
        {
            string replayDir = Path.Combine(root, "replay");
            Directory.CreateDirectory(replayDir);
            string replayPath = Path.Combine(replayDir, "replay-test.png");

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(FrameConverter.ToBitmapSource(okScene)));
            using (var stream = File.Create(replayPath)) encoder.Save(stream);

            var reloaded = FrameFileCodec.LoadGrayFrame(replayPath);
            double replaySimilarity = reloaded is null
                ? 0
                : FrameFeature.Similarity(FrameFeature.Extract(okScene), FrameFeature.Extract(reloaded));

            replayOk = reloaded is not null && replaySimilarity > 0.99;
            replayDetail = $"落盘后重新载入：{(reloaded is null ? "读回失败" : reloaded.Width + "x" + reloaded.Height)}，" +
                           $"特征相似度 {replaySimilarity:P1}";
        }
        catch (Exception ex)
        {
            replayOk = false;
            replayDetail = "回放失败：" + ex.Message;
        }
        check("CCD-013 历史回放复现：画面落盘后重新载入，特征与原始一致（可复现、可追溯）",
            replayOk, replayDetail);

        // ---- CCD-023 边界样本批量（遮挡/噪声 50 次）----
        bool boundaryStable = true;
        for (int i = 0; i < 50; i++)
        {
            var result = recognize.Recognize(FrameFeature.Extract(AddNoise(okScene, 400, 5000 + i)));
            if (result.IsOk == false) { boundaryStable = false; break; }   // 良品被误判成 NG = 误检
        }
        check("CCD-023 边界样本批量：带噪良品连续判定 50 次，无误检（不把良品判成 NG）",
            boundaryStable, "50 次带噪判定未出现误检");

        // ---- CCD-024 设备异常容错 ----
        bool deviceFaultTolerant;
        string deviceDetail;
        try
        {
            var faultCam = new MockCamera(64, 48, 3, "容错测试相机");
            faultCam.ConnectAsync().GetAwaiter().GetResult();
            faultCam.DisconnectAsync().GetAwaiter().GetResult();

            var afterOffline = faultCam.GrabOnceAsync(200).GetAwaiter().GetResult();   // 断开后取帧不该抛异常
            faultCam.ConnectAsync().GetAwaiter().GetResult();
            var afterRecover = faultCam.GrabOnceAsync(500).GetAwaiter().GetResult();

            deviceFaultTolerant = afterOffline is null && afterRecover is not null;
            deviceDetail = afterOffline is null && afterRecover is not null
                ? "断开后取帧安全返回空值，重连后恢复正常出图"
                : "断开或重连后的行为不符合预期";
        }
        catch (Exception ex)
        {
            deviceFaultTolerant = false;
            deviceDetail = "设备异常处理抛异常：" + ex.Message;
        }
        check("CCD-024 设备异常容错：相机断开后取帧不抛异常，重连后恢复出图",
            deviceFaultTolerant, deviceDetail);

        // ---- 其余用例：如实标注映射关系或未实现，不假装通过 ----
        note("CCD-005 模板/轮廓匹配 → 映射到 S1~S3、T1~T4（视觉算法与流程判定）", "已在保底层用例中覆盖");
        note("CCD-006 尺寸测量精度 → 需实机标定基准（算法已内置，精度须现场核定）", "现场项：需标定板与实机");
        note("CCD-008 颜色/特征识别 → 本项目为黑白相机，等价能力由 U1/U2（特征区分）覆盖", "黑白域：以特征区分替代颜色");
        note("CCD-010 OK/NG 判定与日志一致 → 映射到 T1~T4、T8（判定与流程/历史一致）", "已在保底层用例中覆盖");
        note("CCD-011 多条件分支、无死循环 → 映射到 S1~S3（有序步骤 + 集合覆盖 + 超时）", "已在过程层用例中覆盖");
        note("CCD-015 界面状态联动（绿/红/灰）→ 已实现统一状态键，需人工目视确认配色", "界面项：建议现场目视验收");
        note("CCD-016 NG 图像回看（保留 3 帧）→ 已实现；无画面时无法自动化断言", "界面项：需连接相机后目视验收");
        note("CCD-019 界面模板切换 → 功能未开发（方案 11.3 自定义能力，P2）", "未实现：待确认是否要投");
        note("CCD-020/021 人机协同 SOP 联动 → 映射到 S2/S4（跳步锁线、快掠不达标）与保持图闭环", "联动逻辑已覆盖，语音告警未实现");
        note("CCD-022 检测数据联动报表 → 映射到 T8、历史查询与 CSV 导出", "已覆盖：历史查询页可统计与导出");
        note("CCD-025 高低温/72 小时稳定性 → 必须现场长跑，沙箱无法替代", "现场项：需 72h 连续运行");
        note("CCD-026 MES / 上位机接口联动 → MES 接口未开发；PLC 侧契约（规则码）已实现并测过", "部分未实现：待确认是否接 MES");
    }

    private int Finish(string root, string? reportPath)
    {
        int passed = _cases.Count(c => c.Passed && !c.Skipped);
        int skipped = _cases.Count(c => c.Skipped);
        int failed = _cases.Count(c => !c.Passed && !c.Skipped);
        int total = _cases.Count;
        bool allOk = total > 0 && failed == 0;

        var sb = new StringBuilder();
        sb.AppendLine("================ VisionForge 监测逻辑自检报告 ================");
        sb.AppendLine($"时间      ：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"数据目录  ：{root}");
        sb.AppendLine($"触发次数  ：{_triggers}");
        sb.AppendLine();
        sb.AppendLine("---------------- 用例结果 ----------------");
        foreach (var c in _cases)
        {
            string tag = c.Skipped ? "SKIP" : (c.Passed ? "PASS" : "FAIL");
            sb.AppendLine($"[{tag}] {c.Name}");
            if (!string.IsNullOrWhiteSpace(c.Detail)) sb.AppendLine($"        {c.Detail}");
        }
        sb.AppendLine();
        sb.AppendLine("---------------- 执行轨迹 ----------------");
        sb.Append(_trace);
        sb.AppendLine();
        sb.AppendLine("---------------- 结论 ----------------");
        sb.AppendLine($"通过 {passed} 项 · 跳过 {skipped} 项 · 失败 {failed} 项（共 {total} 项）");
        sb.AppendLine(allOk
            ? (failed == 0 ? "没有失败项 —— 可交付范围内的用例全部通过" : "存在失败项")
            : $"存在 {failed} 项失败，需修复后重测");
        sb.AppendLine("=============================================================");

        string text = sb.ToString();
        Console.WriteLine(text);

        try
        {
            string target = string.IsNullOrWhiteSpace(reportPath)
                ? Path.Combine(root, "selftest-report.txt")
                : reportPath!;

            string? dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(target, text, new UTF8Encoding(true));
            Console.WriteLine($"[自检] 报告已写入：{target}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[自检] 报告写入失败：" + ex.Message);
        }

        return allOk ? 0 : 1;
    }

    // ==================================================================
    // 自检用的假客户端：专门验证"断网续传"这条链路（不真的连 MES）
    // ==================================================================

    /// <summary>模拟"网络断了 / MES 没响应"：上传一律失败，且标记为值得重试。</summary>
    private sealed class AlwaysFailMesClient : IMesClient
    {
        public string Name => "自检：模拟断网";
        public bool IsEnabled => true;

        public Task<MesUploadResult> UploadAsync(MesUploadPayload payload, CancellationToken ct = default)
            => Task.FromResult(MesUploadResult.Fail(0, "自检模拟：网络不可达", retry: true));

        public Task<MesUploadResult> TestAsync(CancellationToken ct = default)
            => Task.FromResult(MesUploadResult.Fail(0, "自检模拟：网络不可达"));
    }

    /// <summary>模拟"网络恢复 / MES 正常"：上传一律成功。</summary>
    private sealed class AlwaysOkMesClient : IMesClient
    {
        public string Name => "自检：模拟网络恢复";
        public bool IsEnabled => true;

        public Task<MesUploadResult> UploadAsync(MesUploadPayload payload, CancellationToken ct = default)
            => Task.FromResult(MesUploadResult.Ok("自检模拟：上传成功"));

        public Task<MesUploadResult> TestAsync(CancellationToken ct = default)
            => Task.FromResult(MesUploadResult.Ok("自检模拟：连接正常"));
    }
}
