# DeskTidy 1.1 技术方案

> 目标：在 1.0 的功能基础上，**同时**实现「窗口在最下层（不遮挡任何程序）」和「点击显示桌面时窗口不消失」。

---

## 一、1.0 的结论（已验证）

三个方向都试过，结论如下：

| 方案 | 效果 | 问题 |
|---|---|---|
| 普通窗口层级 | 不遮挡其他窗口 ✓ | Win+D 时被桌面层盖住 ✗ |
| `Topmost`（WS_EX_TOPMOST） | Win+D 时可见 ✓ | 遮挡所有窗口 ✗ |
| `SetWindowPos(HWND_BOTTOM)` | 理论上不遮挡 | 沉到了**桌面层之下**，直接看不见 ✗ |

**根本原因**：Windows 的「显示桌面」不是隐藏窗口，而是把桌面壁纸层（`WorkerW`）提到 z-order 最前。
所以「不被遮挡」和「Win+D 可见」在普通窗口模型里是互斥的。

**唯一出路**：把窗口挂成桌面壁纸层的**子窗口**（Rainmeter 的 on-desktop 模式）。

---

## 二、1.0 尝试挂载失败的原因

按标准流程做了：

1. `SendMessageTimeout(progman, 0x052C, 0xD, 1, ...)` 让 Progman 生成壁纸层
2. 遍历 `WorkerW`，找挂着 `SHELLDLL_DefView` 的那个，它的下一个兄弟就是壁纸层
3. 把样式从 `WS_POPUP` 改成 `WS_CHILD`，`SetParent(hwnd, workerw)`

**查找逻辑是对的**，日志确认：

```
02:12:11 progman=65808 workerw=985072
  -> SetParent done, now parent=65552   ← 期望 985072，实际是 Progman
```

**失败点**：`SetParent` 之后父窗口仍然是 Progman（类名 `#32769`）。

### 怀疑的三个原因

1. **漏了 Hide/Show**：标准做法是 `ShowWindow(SW_HIDE)` → 改样式 → `SetParent` → `ShowWindow(SW_SHOW)`，
   直接改样式可能被系统忽略
2. **WPF 分层窗口不兼容**：`AllowsTransparency=True` 的窗口是 `WS_EX_LAYERED`，
   可能不支持成为子窗口
3. **HwndSource 重置**：WPF 内部可能在挂载后重建了窗口句柄

---

## 三、1.1 的实现路线

### 路线 A：修好 WPF + SetParent（改动最小，优先尝试）

```csharp
// 正确的挂载顺序
Native.ShowWindow(hwnd, SW_HIDE);                      // 1. 先隐藏
int style = Native.GetWindowLong(hwnd, GWL_STYLE);
style = (style & ~WS_POPUP) | WS_CHILD;                // 2. 转成子窗口样式
Native.SetWindowLong(hwnd, GWL_STYLE, style);
Native.SetParent(hwnd, workerw);                       // 3. 挂上去
Native.ShowWindow(hwnd, SW_SHOW);                      // 4. 再显示
```

**关键验证**：挂载后立刻 `GetAncestor(hwnd, GA_PARENT)`，确认等于 workerw。

如果还失败，检查 `AllowsTransparency`：
- 尝试关掉 `AllowsTransparency`（用 `WindowChrome` 做圆角），再挂载
- 如果关掉后能挂上，说明就是分层窗口的问题

### 路线 B：WinForms 宿主 + WPF 内容

用 `ElementHost` 把 WPF 的 `UserControl` 塞进 WinForms 窗口。
WinForms 窗口对 `SetParent` 的兼容性比 WPF 窗口好得多。

代价：要重写窗口外壳（无边框、拖动、阴影），但内容层（分区、拖拽、菜单）可以整个复用。

### 路线 C：纯 Win32 宿主 + WPF HwndSource

创建原生窗口，用 `HwndSource` 把 WPF 内容渲染进去。
最灵活但工作量最大。

---

## 四、1.1 的验证清单

挂载成功与否，逐条确认：

- [ ] `GetAncestor(hwnd, GA_PARENT)` == 找到的 WorkerW 句柄
- [ ] 桌面图标（SHELLDLL_DefView）仍然正常显示
- [ ] 窗口在壁纸之上可见
- [ ] 打开浏览器/编辑器，它们能正常盖住分区条
- [ ] Win+D 后分区条依然可见
- [ ] 点击分区条能正常响应（展开、拖拽、右键、双击图标）
- [ ] 拖拽文件进出仍然正常
- [ ] 切换壁纸后窗口不消失（WorkerW 可能重建）

---

## 五、需要注意的坑

1. **WorkerW 会重建**：切换壁纸、重启 Explorer、插拔显示器后，WorkerW 可能被销毁重建，
   窗口会跟着消失。需要监听 `WM_SETTINGCHANGE` 或者定时检查父窗口是否还活着，必要时重新挂载。

2. **坐标变成相对父窗口**：`SetParent` 后窗口坐标是相对于 WorkerW 的，
   如果 WorkerW 不在 (0,0)，需要补偿。

3. **失去部分窗口行为**：挂成子窗口后，`Alt+Tab` 里不会有它、`Topmost` 失效、
   某些 DWM 效果（阴影、圆角）可能不生效。

4. **多显示器**：每个显示器有自己的 WorkerW 层级，跨屏时可能出错。

5. **退出时要还原**：`SetParent(hwnd, NULL)` 再关闭，否则可能留下孤儿窗口。

---

## 六、1.0 的代码基线

- 分支：`release/1.0`（冻结）、`main`
- Tag：`v1.0.0`
- 关键文件：
  - `MainWindow.xaml.cs` — 窗口行为、拖拽、菜单、分区逻辑（79 KB，最核心）
  - `Native.cs` — 所有 Win32 P/Invoke
  - `TrayIcon.cs` — 原生托盘
  - `Frost.cs` — 壁纸模糊底图
  - `Storage.cs` / `Models.cs` — 数据层

---

## 七、给 1.1 的建议

**先验证路线 A 的可行性，再决定要不要重写。**

具体做法：写一个 20 行的最小 demo —— 一个 WPF 空窗口，挂到 WorkerW，
用 Spy++ 或者 `GetAncestor` 检查父窗口是否正确。

- 如果挂上了 → 直接在 1.0 代码上改，工作量 1-2 小时
- 如果挂不上 → 试试关掉 `AllowsTransparency` 再挂
- 还是不行 → 才考虑路线 B/C 重写

**不要一上来就重写**，很可能只是漏了 Hide/Show 这一步。
