# VisionForge「追觅洗地机AI-SOP监测系统」· MES 对接说明

> 给 MES 供应商 / 信息部的对接文档。
> 软件侧已经**留好接口**：只要 MES 提供一个能收 JSON 的 HTTP 接口，就能把每个工位的监测数据接过去。

---

## 一、一句话说清对接方式

**每个工位端**（装上本软件的工控机）在做完整件产品后，向 MES 发一个 HTTP POST：

```
POST  <MES 接口地址>
Content-Type: application/json; charset=utf-8
Authorization: Bearer <令牌，可留空>

{ …上传体见第二节… }
```

- 成功判定：HTTP `2xx`
- 失败处理：网络错误/超时/`5xx` → 自动进本地队列，之后每 30 秒补发一次；
  `4xx` → 认为字段/权限不匹配，不再重试，写入 `data\mes-queue\rejected\` 留证据给人工排查。
- 幂等：上传体里的 `ReportId` 是唯一号，MES 侧按它去重即可（重发不会产生重复记录）。
- 断网不影响生产：数据先落本地，联网后自动补发；界面「系统设置 → MES 上传」里能看到待发积压条数。

---

## 二、上传体（固定契约，SchemaVersion 1.0）

```json
{
  "SchemaVersion": "1.0",
  "ReportId": "9f2c1d3e4b5a6c7d8e9f0a1b2c3d4e5f",
  "StationCode": "ST-07",
  "StationName": "主机装配 7 号位",
  "LineName": "Line-03",

  "PieceId": "20260912-0007",
  "BatchNo": "20260912",
  "ProductModel": "MIRROR_DEMO",
  "RecipeName": "洗地机主机装配",
  "Operator": "张三",

  "StartedAt": "2026-09-12 21:50:02",
  "FinishedAt": "2026-09-12 21:50:33",
  "CycleSec": 31.0,

  "Verdict": "OK",
  "ViolationCode": -1,
  "JudgementReason": "整件工序已全部完成",

  "Steps": [
    { "Seq": 1, "Name": "取件并放入工装", "StartedAt": "2026-09-12 21:50:02",
      "FinishedAt": "2026-09-12 21:50:07", "DurationSec": 5.1, "StandardSec": 5.0, "IsOk": true }
  ]
}
```

### 字段说明

| 字段 | 类型 | 说明 |
|---|---|---|
| `SchemaVersion` | string | 契约版本。以后只加字段不删字段，靠它区分 |
| `ReportId` | string | 本次上报唯一号（32 位十六进制），**MES 侧按它幂等去重** |
| `StationCode` | string | 工位编号（建议与 MES 的工位编码一致） |
| `StationName` / `LineName` | string | 工位名 / 产线名 |
| `PieceId` | string | 件号（`批次-序号`），一件产品唯一 |
| `BatchNo` | string | 批次号 |
| `ProductModel` | string | 产品型号 |
| `RecipeName` | string | 使用的检测配方名 |
| `Operator` | string | 操作员 |
| `StartedAt` / `FinishedAt` | string | `yyyy-MM-dd HH:mm:ss`（本机时间） |
| `CycleSec` | number | 整件节拍（秒） |
| `Verdict` | string | `OK` / `NG` |
| `ViolationCode` | number | 违规码，**与写入 PLC 的规则码完全一致**；OK 时为 `-1` |
| `JudgementReason` | string | 判定说明（中文，直接可读） |
| `Steps[]` | array | 每道工序明细（见下） |

### 违规码对照（`ViolationCode`）

| 值 | 含义 |
|---|---|
| `-1` | 无违规（OK） |
| `0` | 跳步（某道工序没做就结束了） |
| `1` | 错序（还没做当前工序就先做后面的） |
| `2` | 目标未完成（动作没做到位 / 东西没放到位） |
| `3` | 数量不符 |
| `4` | 关键点丢失（人手被遮挡看不清，**报警请人工确认，不算违规**） |
| `5` | 规则配置错误 |

### 工序明细 `Steps[]`

| 字段 | 类型 | 说明 |
|---|---|---|
| `Seq` | number | 第几道工序（从 1 开始） |
| `Name` | string | 工序/动作名 |
| `StartedAt` / `FinishedAt` | string | 该动作的开始/结束时刻 |
| `DurationSec` | number | 该动作用时（秒） |
| `StandardSec` | number? | 标准工时（秒），没配则为 `null` |
| `IsOk` | bool | 该工序是否合格 |

---

## 三、软件侧怎么配

工位端：**系统设置 → 工位联网 / MES 对接**

| 配置项 | 填什么 |
|---|---|
| 启用上传到 MES | 勾上 |
| 接口地址 | MES 提供的接收地址，如 `http://mes-server/api/sop/upload` |
| 令牌 | 若 MES 侧要求鉴权，填在这里（会以 `Authorization: Bearer <令牌>` 发送） |
| 超时(秒) | 默认 5 |
| 同时在本地留一份 JSON | 建议勾上：`data\mes-outbox\` 会留一份完全一样的 JSON，方便核对 |

改完点最下面的 **保存设置**（立即生效，不用重启）。

**测试连接**按钮会发一条 `StationCode=TEST` 的探测数据，MES 侧可以据此确认字段能否解析、并把它忽略掉。

---

## 四、常见问题

**Q：我们的 MES 不是 HTTP 接口，是 WebService / 数据库直写 / MQTT 怎么办？**
A：软件里 MES 上传做成了一个**可替换的接口**（`IMesClient`）。现场只要按你们的协议再写一个实现类，
主程序、界面、配置都不用动 —— 和"换相机 / 换算法插件"是同一套做法。
当前已内置两种实现：HTTP/JSON（上面这套）、本地文件（没有 MES 时先落盘）。

**Q：几十个工位同时上传，MES 扛得住吗？**
A：每个工位是"一件产品一条"，按 30 秒一件算，100 个工位约 3.3 条/秒 —— 压力很小。
客户端还带 30 秒补发定时器与积压队列，MES 短时重启也不会丢数据。

**Q：MES 侧要不要按工位区分？**
A：每条都带 `StationCode` 与 `LineName`，直接按这两个字段归档即可。
同一件产品可能跨多个工位，用 `PieceId` 串起来就是完整的追溯链。

**Q：界面上的实时看板呢？**
A：那是**车间内部**用的，不走 MES：任意一台电脑勾上"本机作为汇总终端"，
其他工位填上它的地址即可；浏览器打开 `http://<那台电脑IP>:8088/` 就能看全场实时数据
（页面每 2 秒自动刷新，也支持从 `/api/stations` 取同样的数据做二次开发）。
