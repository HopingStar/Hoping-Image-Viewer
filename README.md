# 图片查看器（C# 后端 + HTML 前端）

一个本地图片查看器：**C# / ASP.NET Core Minimal API** 做后端，**HTML/CSS/JS** 做前端。支持**相册视图**（按子文件夹分组）、**列表视图**、图片的**放大缩小 / 平移 / 旋转**，并可将旋转后的图片导出保存。

提供两种运行方式：**🖥️ 桌面窗口版**（WPF + WebView2 内嵌，不依赖浏览器，推荐）与 **🌐 浏览器版**（`--urls` 固定端口）。

技术栈与工作区既有项目（TouhouRoleApi_c、tmod_manager_c 等）保持一致：.NET 8、Minimal API、`/api/*` snake_case JSON、ImageSharp 图像处理、华为云 NuGet 镜像源。

## 功能特性

- 🖥️ **桌面窗口版**：WPF + WebView2 内嵌渲染，双击即出独立窗口，**不依赖浏览器**（Windows 10/11 自带 WebView2 运行时）
- 🗂 **相册页 / 图片页分离**：相册页**只显示相册**（「我的相册」链接 + 子文件夹相册），不混入图片；**点击相册直接跳转到该相册的专属图片页**，专门显示该相册的图片（含递归统计的图片总数）。「⬆ 上级」从图片页返回相册页
- 📁 **添加相册文件夹（直接链接，不复制）**：「我的相册」区 + 顶部「📁 添加文件夹」按钮（桌面版弹系统文件夹选择框），**浏览并导入任意位置的图片文件夹**——只记路径直接链接，相册文件仍留在原地；列表持久化到用户目录，重启自动加载。添加后直接进入该相册的图片页
- 🏠 **首页**：一键回到「我的相册」相册页；可随时「✕」移除某个链接（不删除文件夹本身）
- 🔲 **模态弹窗**：所有提示/确认/输入统一为应用内模态弹窗（无浏览器 alert/confirm/prompt）；**禁用右键菜单**；隐藏网页特征（禁止文本选择/图片拖拽、桌面版关闭 DevTools/缩放/状态栏/浏览器快捷键）
- 🏷 **图片标签**：右键任意图片 → 弹出菜单 →「添加标签」→ 右侧子菜单列出标签，**当前图片已有的标签用紫色框高亮**，点击即可添加/移除（也可直接新建）；顶部「🏷 标签」按钮进入标签管理页：**新建/删除标签**、**多选标签筛选图片（取交集）**。标签主题色统一为**紫色**
- 🔍 **标签搜索**：右键「添加标签」右侧子菜单顶部带**搜索框**（打开时自动聚焦），输入关键词即可实时过滤标签，方便在大量标签中快速定位
- 🤖 **识别角色**：右键图片或查看器「🔍 识别」按钮，调用**你配置的角色识别 API**（如本工作区的 TouhouRoleApi_c：`POST /api/predict/file` → `{ top:[{class,confidence}] }`），识别图中角色并显示 top 候选与置信度，**可一键把角色加为标签**；API 地址在识别面板内填写，持久化到程序同目录 `data/ai.json`
- ⚡ **一键识别相册**：打开相册后工具栏出现「⚡ 一键识别」（与平铺/列表一样仅在图片页显示），自动逐张识别**相册全部图片**，弹窗显示**进度 + 当前文件与完整路径**（可取消）；识别完弹**预览界面**：按角色标签分组（单选标签查看匹配图片），**点击图片可修改识别错误的标签**（下拉选已有标签或输入新名）；点「**确定写入**」才批量写标签（图片已含该标签自动跳过），点「取消」则不写入
- 🖱️ **拖入图片**：把任意图片文件直接拖进窗口，松开即在查看器中显示（无需添加到相册）
- 🎞️ **GIF 动图**：支持 GIF 文件，**缩略图也是动图**（直接返回原 GIF 而非静态帧）
- ☰ **列表视图**：表格列出缩略图 / 名称 / 大小 / 修改时间
- 🔍 **放大缩小**：鼠标滚轮（图片未超出界面时**以中心缩放**，超出后**以鼠标为中心**）、➕➖ 按钮、双击切换（适应窗口 ↔ 2 倍）、适应窗口、实际大小；**平滑缩放动画**（150ms 缓动），灵敏度适中
- 🚀 **虚拟化渲染**：相册图片网格**只渲染视口附近**（滚动时动态增删），上万张的相册也能秒开、不爆内存；列表视图分块加载
- 🗂 **图片排序**：打开相册后工具栏出现排序控件（仅图片页显示）：按**名称 / 修改时间 / 创建时间 / 大小**排序，支持**升序 / 降序**一键切换（默认名称升序）
- ✋ **平移**：放大后按住鼠标拖拽
- ↻ **旋转**：左右各 90°，实时预览；**「保存」按钮**把旋转后的图片导出下载（后端 ImageSharp 生成）
- 🧭 **切换**：◀▶ 按钮 / 键盘 ← → 遍历当前目录全部图片；Esc 关闭

## 目录结构

```
ImageViewer_c/
├── README.md                      # 本文档
├── NuGet.config                   # 华为云 NuGet 镜像源
├── ImageViewer.sln                # 解决方案
├── Release/                       # 🖥️ 发布产物：双击 HopingImageViewer.exe 即用（含前端，不含示例图/图标文件）
├── pictures/                      # 仅开发期示例图片目录（发布版不捆绑，相册由「添加文件夹」链接外部目录）
├── tools/
│   └── make_samples.ps1           # 生成示例图片的脚本（System.Drawing，可重新运行）
└── src/
    ├── ImageViewer/               # ASP.NET Core Web 项目（net8.0，后端 + 前端 wwwroot）
    │   ├── ImageViewer.csproj
    │   ├── appsettings.json       # Images:Root 默认目录配置
    │   ├── Program.cs             # 浏览器版入口（--urls 固定端口）
    │   ├── Gallery/               # 图片浏览核心
    │   │   ├── AppHost.cs         # ★ Web 应用组装（静态前端 + /api/* 端点），两种宿主共用
    │   │   ├── ImageService.cs    # 目录扫描 / 缩略图(缓存) / 旋转导出 / 自然排序
    │   │   ├── AlbumStore.cs      # 已链接相册列表持久化（程序同目录 data/albums.json）
    │   │   ├── TagStore.cs        # 图片标签持久化（程序同目录 data/tags.json）
    │   │   ├── AiConfigStore.cs   # 角色识别 API 地址持久化（程序同目录 data/ai.json）
    │   │   └── Models.cs          # PhotoInfo / AlbumInfo / FolderListing
    │   └── wwwroot/               # 前端（HTML/CSS/JS）
    │       ├── index.html
    │       ├── css/style.css
    │       └── js/app.js
    └── ImageViewer.App/           # 🖥️ 桌面窗口版（WPF + WebView2 + 内嵌 Kestrel 随机端口）
        ├── ImageViewer.App.csproj
        ├── App.xaml(.cs)          # 启动内嵌宿主 → 建窗 → 退出时停止
        ├── MainWindow.xaml(.cs)   # WebView2 渲染前端
        └── Hosting/WebHostHandle.cs  # 随机端口读取 + 优雅停止（防主线程死锁）
```

## 快速开始

**前提**：.NET SDK 8.0+（`dotnet --version` 检查）。

### 🖥️ 桌面窗口版（推荐，不依赖浏览器）

**直接双击 exe**：打开 `Release\HopingImageViewer.exe` 即弹出界面（发布版已内置前端与示例图片，自包含）。也可以把它发送到桌面/固定到任务栏。

或双击仓库根 `start.bat`（优先启动 Release exe，没有则编译源码运行）；或源码运行：

```bash
dotnet run --project src/ImageViewer.App -c Release
```

弹出独立窗口「Hoping Image Viewer」，WebView2 渲染前端，**无需打开浏览器**；关闭窗口即退出。首次打开没有相册时，点「＋ 添加文件夹」浏览并导入你的图片文件夹即可——**只记路径直接链接，文件不移动、不复制**，列表持久化到用户目录，下次启动自动加载。

> 重新发布：运行 `powershell -ExecutionPolicy Bypass -File packaging\build.ps1` 一键打包（详见下文「打包发布」）。手动发布：`dotnet publish src/ImageViewer.App -c Release -r win-x64 --self-contained true -o <输出目录>`（自包含，免装 .NET）。

### 🌐 浏览器版（备选）

```bash
cd src/ImageViewer
dotnet run --urls http://localhost:5211
```

浏览器打开 <http://localhost:5211> 即可。换端口改 `--urls` 参数即可（如 `http://localhost:5212`）。

**默认图片目录**：`appsettings.json` 的 `Images:Root`；留空时开发版定位到仓库根 `pictures/`（示例数据），**发布版不再捆绑示例图**——通过「📁 添加文件夹」链接你任意位置的相册目录即可。

## 打包发布

一键打包（自包含，目标机免装 .NET 8）：

```bash
powershell -ExecutionPolicy Bypass -File packaging\build.ps1
```

产物在 `packaging\dist\`：

| 产物 | 说明 |
|---|---|
| `HopingImageViewer-portable-1.0.0.zip` | **zip 便携版**：解压即用，数据保存在目录内 `data/`，可整体拷贝（绿色免安装） |
| `HopingImageViewer-setup-1.0.0.exe` | **安装程序**：支持**选择安装路径**；勾选「**绿色安装**」= 便携模式（不创建卸载程序/注册表/快捷方式，可拷贝目录）；**不勾选** = 普通安装（创建**卸载程序**、卸载注册表项、开始菜单与可选桌面快捷方式） |

打包脚本与安装脚本：`packaging\build.ps1`、`packaging\HopingImageViewer.iss`（Inno Setup）。依赖 7-Zip 与 Inno Setup 6。

## 使用说明

| 操作 | 方式 |
|---|---|
| 添加相册文件夹 | 点「📁 添加文件夹」/「我的相册」区「＋ 添加文件夹」（桌面版弹系统文件夹选择框），浏览并导入任意目录（**直接链接，不复制**），添加后直接进图片页 |
| 查看相册 | 「🏠 首页」回到相册页（只显示相册）；**点相册卡片跳转到该相册的专属图片页**；卡片「✕」可移除链接（不删文件夹） |
| 返回相册页 | 图片页点「⬆ 上级」返回上一级（回到相册根即显示相册页） |
| 平铺 / 列表切换 | 打开相册后（图片页）右上角「🗂 平铺」「☰ 列表」；相册页始终以平铺显示相册 |
| 图片排序 | 打开相册后工具栏下拉选**名称 / 修改时间 / 创建时间 / 大小**，「↑ / ↓」切换升序降序（仅图片页） |
| 打开图片 | 相册视图点缩略图 / 列表视图点行 |
| 放大 / 缩小 | 鼠标滚轮、➕➖ 按钮 |
| 平移 | 放大后按住鼠标拖拽 |
| 旋转 | 「↺ 左旋 / ↻ 右旋」按钮（每次 90°）；「⟲」复位旋转 |
| 导出旋转图 | 「💾 保存」→ 下载 `rotated_<原名>` |
| 上一张 / 下一张 | ◀▶ 按钮或键盘 ← → |
| 适应窗口 / 实际大小 | ⤢ / 1:1 按钮，或双击图片切换 |
| 识别角色 | 查看器「🔍 识别」按钮或右键图片 →「识别角色」→ 面板显示 top 候选与置信度，可一键把角色加为标签；面板顶部输入角色识别 API 地址点「保存」即可（持久化） |
| 一键识别相册 | 打开相册后点工具栏「⚡ 一键识别」→ 进度弹窗 → 识别完预览界面按标签核对、点图片可改标签 →「确定写入」才保存（取消则不写入） |
| 关闭查看器 | ✕ 按钮或 Esc |

键盘快捷键（查看器打开时）：`←` `→` 切换、`Esc` 关闭、`+` `-` 缩放、`0` 实际大小、`r` 复位旋转。

## HTTP API 文档

JSON 为 snake_case。`path` 参数：空 = 默认图片目录；相对路径按默认目录解析；绝对路径直接用。

| 方法 | 端点 | 作用 |
|---|---|---|
| GET | `/api/photos?path=<dir>&root=<dir>` | 列目录：直接图片 `photos` + 子文件夹相册 `albums`（`root` 处 `is_root=true` 不可再回退；默认目录缺失返回空列表） |
| GET | `/api/photo?path=<file>` | 原图流 |
| GET | `/api/thumb?path=<file>&max=256` | 缩略图（JPEG，内存缓存，max 32~512） |
| GET | `/api/photo/export?path=<file>&rotate=90` | 旋转（顺时针 0/90/180/270）后下载 |
| GET | `/api/albums` | 列出已链接相册（外部目录直接链接）：`{ "albums": [{name,path,count,cover_thumb_url}] }`，不存在的目录自动跳过 |
| POST | `/api/albums`  body `{ "path": "<dir>" }` | 添加相册链接（校验目录存在后持久化，不枚举不复制） |
| DELETE | `/api/albums?path=<dir>` | 移除相册链接（只删链接，不删文件夹） |
| GET | `/api/tags` | 列出全部已定义标签：`{ "tags": ["风景"] }` |
| GET | `/api/tags/image?path=<file>` | 某张图片的标签 |
| POST | `/api/tags/image` body `{ "path": "<file>", "tag": "风景" }` | 给图片加标签（标签不存在自动创建） |
| DELETE | `/api/tags/image?path=<file>&tag=<tag>` | 移除图片上的标签 |
| POST | `/api/tags` body `{ "name": "风景" }` | 创建标签 |
| DELETE | `/api/tags?name=<tag>` | 删除标签（同时从所有图片移除） |
| GET | `/api/tags/filter?tags=风景,旅行` | 按标签**交集**筛选图片（`tags` 逗号分隔） |
| GET | `/api/ai/config` | 读取角色识别 API 地址：`{ "api_url": "http://localhost:5210" }`（未配置为 null） |
| POST | `/api/ai/config` body `{ "api_url": "<url>" }` | 保存角色识别 API 地址（空值清除），持久化到 `data/ai.json` |
| POST | `/api/ai/recognize?path=<file>` | 把本地图片上传到配置的角色识别 API（`POST /api/predict/file`，兼容 TouhouRoleApi_c）并**透传识别结果** `{ top:[{class,confidence}], count, elapsed_ms }` |

`/api/photos` 返回结构示例：

```json
{
  "path": "D:\\WorkSpace\\...\\pictures",
  "display_name": "pictures",
  "parent": "D:\\WorkSpace\\...",
  "is_root": false,
  "photos": [
    {
      "name": "001.jpg",
      "path": "D:\\...\\示例相册A\\001.jpg",
      "size": 51234,
      "modified": "2026-08-24T08:00:00",
      "url": "/api/photo?path=...",
      "thumb_url": "/api/thumb?path=...&max=256"
    }
  ],
  "albums": [
    {
      "name": "示例相册A",
      "path": "D:\\...\\pictures\\示例相册A",
      "count": 5,
      "cover_thumb_url": "/api/thumb?path=...&max=256"
    }
  ]
}
```

支持的图片格式：jpg / jpeg / png / bmp / gif / tif / tiff / webp。

## 常见问题

| 问题 | 解决 |
|---|---|
| 端口被占用 | 换端口启动：`--urls http://localhost:5212`（桌面版自动用随机端口，无需理会） |
| 图片打不开 / 列表里没有 | 检查扩展名是否在支持列表；损坏文件会被跳过显示报错 |
| 相册/标签存哪了 | 程序同目录 `data/albums.json`、`data/tags.json`（只存路径/标签，不碰相册文件）；`data/ai.json` 存角色识别 API 地址；旧版 `%LOCALAPPDATA%` 数据会自动迁移 |
| 移除了相册文件夹怎么办 | 对应链接自动从「我的相册」消失；重新「添加文件夹」即可 |
| 给图片打标签 | 右键图片 → 「添加标签」→ 右侧子菜单搜索/点标签（当前图标签紫色高亮）；或「🏷 标签」页新建/筛选 |
| 角色识别用不了 | 先运行角色识别 API（如 `dotnet run --project TouhouRoleApi_c/src/TouhouRoleApi --urls http://localhost:5210`），再在查看器「🔍 识别」面板顶部填 API 地址点「保存」；若提示连接失败，检查 API 是否已启动、地址是否可访问 |
| 想换默认目录 | 改 `appsettings.json` 的 `Images:Root` 为绝对路径，重启 |
| 示例图不想要 | 发布版已不捆绑；开发版直接删除仓库 `pictures/` 即可（可运行 `tools/make_samples.ps1` 重新生成） |

## 已知限制

- 无认证鉴权，仅限本地/内网使用
- 缩略图统一输出为 JPEG（原透明 PNG 的缩略图会带底色）
- 超大相册（数千张）网格已虚拟化、列表已分块加载；首次进入的目录扫描（递归计数）仍可能略慢，但页面会即时呈现首屏
