using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Simulation;

/// <summary>
/// 模拟的操作员动作。测试逻辑的核心输入 —— 每个值对应一类真实现场错误。
/// </summary>
public enum SimulatedAction
{
    /// <summary>规范作业：按顺序完成当前工序（应该在位的东西都在位）。</summary>
    CorrectStep = 0,

    /// <summary>漏装 / 未动作：这一步压根没做，就触发了检测。</summary>
    NoAction = 1,

    /// <summary>跳步：跳过当前工序，直接去做后面的工序。</summary>
    SkipStep = 2,

    /// <summary>重复触发：画面完全不变，再触发一次（验证幂等与去抖）。</summary>
    RepeatTrigger = 3,
}

/// <summary>
/// 操作员动作模拟器 —— 没有产线、没有相机也能把整套监测逻辑跑起来。
///
/// <para><b>它到底在做什么：</b>把"人做了哪个动作"翻译成"画面里哪些工位有料"。
/// 例如"规范完成第 3 步" → 第 3 个 ROI 的位置出现一块料。
/// 然后由上位机真的去抓图、真的过一遍视觉算法、真的走 SOP 状态机 ——
/// 中间没有任何一步是"假装"的，只有图像来源是模拟的。</para>
///
/// <para><b>为什么必须这么做：</b>如果模拟相机只是随机撒亮块，
/// ROI 占用就是随机的，判出来的 OK/NG 也是随机的 —— 那样的"测试"只能证明
/// 程序没崩，证明不了"人做错了它能拦住"。动作模拟器让场景<b>完全确定</b>，
/// 于是"漏装必须报警""跳步必须锁线"才成了可断言的事实。</para>
/// </summary>
public sealed class OperatorActionSimulator
{
    private readonly ISceneCamera _camera;
    private readonly object _gate = new();

    private string _recipeId = string.Empty;

    /// <summary>各工位是否已被"作业"过（true = 画面里这个位置有料）。</summary>
    private bool[] _occupied = Array.Empty<bool>();

    /// <summary>模拟操作员的下一个待办工位（= 已完成工序数）。</summary>
    private int _nextIndex;

    private string _lastDescription = "未开始";

    public OperatorActionSimulator(ISceneCamera camera)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
    }

    /// <summary>已完成工序数（模拟操作员的进度）。</summary>
    public int CompletedCount { get { lock (_gate) return _nextIndex; } }

    /// <summary>最近一次动作的中文描述，直接显示给操作员。</summary>
    public string LastDescription { get { lock (_gate) return _lastDescription; } }

    /// <summary>当前场景里"有料"的工位序号（1 起）。</summary>
    public IReadOnlyList<int> OccupiedSteps
    {
        get
        {
            lock (_gate)
            {
                var list = new List<int>();
                for (int i = 0; i < _occupied.Length; i++)
                    if (_occupied[i]) list.Add(i + 1);
                return list;
            }
        }
    }

    /// <summary>回到"一件新产品刚开始"的状态，并把干净画面推给相机。</summary>
    public void Reset(Recipe recipe)
    {
        lock (_gate)
        {
            var rois = EnabledRois(recipe);
            _recipeId = recipe.Id;
            _occupied = new bool[rois.Count];
            _nextIndex = 0;
            _lastDescription = $"已复位：{rois.Count} 道工序等待作业";
            Push(recipe);
        }
    }

    /// <summary>
    /// 执行一次模拟动作：改画面 + 返回中文描述。
    /// 注意它<b>不</b>触发检测 —— 检测由上层触发，这样"动作"和"拍照判定"
    /// 在测试里是两个独立步骤，和现场"手离开 → 光电触发"的时序一致。
    /// </summary>
    public string Perform(Recipe recipe, SimulatedAction action)
    {
        lock (_gate)
        {
            var rois = EnabledRois(recipe);
            if (_recipeId != recipe.Id || _occupied.Length != rois.Count)
            {
                _recipeId = recipe.Id;
                _occupied = new bool[rois.Count];
                _nextIndex = 0;
            }

            int n = rois.Count;
            if (n == 0)
            {
                _lastDescription = "配方里没有启用任何 ROI，无法模拟动作";
                return _lastDescription;
            }

            int current = Math.Min(_nextIndex, n - 1);

            switch (action)
            {
                case SimulatedAction.CorrectStep:
                    if (_nextIndex >= n)
                    {
                        _lastDescription = "全部工序已完成，等待下料";
                        break;
                    }
                    _occupied[_nextIndex] = true;
                    _lastDescription = $"规范作业：完成第 {_nextIndex + 1} 道「{rois[_nextIndex].Name}」";
                    _nextIndex++;
                    break;

                case SimulatedAction.NoAction:
                    _lastDescription =
                        $"漏装/未动作：第 {current + 1} 道「{rois[current].Name}」没有动作，直接触发检测";
                    break;

                case SimulatedAction.SkipStep:
                    if (_nextIndex >= n - 1)
                    {
                        _lastDescription = "已是最后一道工序，无法演示跳步";
                        break;
                    }
                    int skipTo = _nextIndex + 1;
                    _occupied[skipTo] = true;
                    _lastDescription =
                        $"跳步：跳过第 {_nextIndex + 1} 道「{rois[_nextIndex].Name}」，" +
                        $"直接做第 {skipTo + 1} 道「{rois[skipTo].Name}」";
                    break;

                case SimulatedAction.RepeatTrigger:
                    _lastDescription = "重复触发：画面不变（应判为「进行中」，不得推进工序）";
                    break;
            }

            Push(recipe);
            return _lastDescription;
        }
    }

    // ------------------------------------------------------------------
    private static List<RoiRegion> EnabledRois(Recipe recipe) =>
        recipe.Rois.Where(r => r.Enabled).ToList();

    /// <summary>把当前占用状态转成相机场景。块缩到 ROI 的 80%，避免相邻工位粘连。</summary>
    private void Push(Recipe recipe)
    {
        var rois = EnabledRois(recipe);
        var blobs = new List<SceneBlob>();

        for (int i = 0; i < rois.Count && i < _occupied.Length; i++)
        {
            if (!_occupied[i]) continue;

            var r = rois[i];
            double w = r.Width * 0.8;
            double h = r.Height * 0.8;

            blobs.Add(new SceneBlob
            {
                X = r.X + (r.Width - w) / 2.0,
                Y = r.Y + (r.Height - h) / 2.0,
                Width = w,
                Height = h,
            });
        }

        _camera.SetScene(blobs);
    }
}
