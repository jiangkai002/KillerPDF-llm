# KillerPDF-llm

一款面向 Windows 的本地 PDF 阅读、编辑与 AI 辅助工具。本项目基于开源项目 [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) 二次开发，在原有 PDF 查看、编辑、标注和 OCR 能力之上，增加了大模型对话、截图提问、Markdown 渲染、笔记总结及历史会话等功能。

> 本仓库是经过修改的社区 Fork，并非 KillerPDF 上游官方版本。

## 项目特点

- 使用 WPF 和 .NET 8 开发，界面基于 WPF UI 优化
- 支持发布为 Windows x64 自包含单文件 EXE
- PDF 阅读、编辑、标注、合并、拆分、签名和表单填写
- 内置 PP-OCRv5，增强中文、英文及中英混排识别效果
- 支持 Tesseract 作为备用 OCR 引擎
- 可框选 PDF 页面区域，截图并识别文字后向大模型提问
- 支持多个 OpenAI 兼容模型配置，并可在对话框中随时切换
- 大模型回答采用流式输出
- 支持 Markdown、代码块、表格和 LaTeX 数学公式渲染
- 可让大模型根据当前对话重新归纳并生成 Markdown 笔记
- 支持本地保存、查看、删除和回放历史对话
- 中文界面统一使用微软雅黑

## AI PDF 助手

打开右侧的 AI PDF Assistant 后，可以直接针对文档提问，也可以使用“Screenshot & OCR”框选 PDF 内容。识别出的文字会作为文档上下文发送给当前选择的大模型。

模型配置支持以下字段：

- 配置名称
- API Endpoint
- Model Name
- API Key

可以保存多个配置，例如 OpenAI、兼容 OpenAI Chat Completions API 的本地模型服务或第三方模型服务。

默认请求地址由配置的 Endpoint 加上 `/chat/completions` 组成。例如：

```text
Endpoint: https://api.openai.com/v1
Request:  https://api.openai.com/v1/chat/completions
```

## OCR

项目默认使用内置的 PP-OCRv5 ONNX 模型，模型和运行时会随单文件程序一起发布，首次使用时释放到本地缓存。它更适合中文文档、扫描件和中英混排内容。

OCR 可用于：

- 整页文字识别
- 框选区域识别
- PDF 截图后向大模型提问
- 生成可搜索 PDF
- 导出文档文字

## Markdown 与数学公式

AI 回答使用 MarkdView 渲染 Markdown，支持标题、列表、表格、引用和代码块。LaTeX 公式由 WpfMath 在本地渲染，可识别常见写法：

```markdown
行内公式：$E = mc^2$

块级公式：
$$
x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}
$$
```

## AI 笔记

“Save note”不会直接复制聊天记录。应用会把当前问答再次交给所选模型，让模型去除寒暄和重复内容，并重新整理知识点、推理过程、代码、公式、注意事项与结论，最后保存为本地 `.md` 文件。

## 历史对话

AI 面板顶部提供保存和历史记录按钮。保存内容包括问题、回答、PDF 来源、模型配置 ID 和时间，但不会保存 API Key。

历史文件默认位于：

```text
%LOCALAPPDATA%\KillerPDF\AiConversations.json
```

最多保留最近 100 条记录。打开历史记录后可以恢复完整上下文并继续提问。

## 隐私说明

PDF 编辑、OCR 和历史记录存储均在本地完成。使用 AI 功能时，用户问题以及所附加的 OCR 文本会发送到用户配置的模型 Endpoint。数据如何处理取决于对应的模型服务提供方，请勿向不受信任的服务发送敏感文档。

模型配置保存在当前 Windows 用户的本地设置中，历史对话保存在 `%LOCALAPPDATA%\KillerPDF`。项目不会把 API Key 写入历史对话文件。

## 系统要求

- Windows 10 或 Windows 11，x64
- 源码编译需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 使用自包含发布的最终用户不需要安装 .NET Runtime

## 从源码运行

```powershell
git clone https://github.com/jiangkai002/KillerPDF-llm.git
cd KillerPDF-llm
dotnet restore
dotnet run --project KillerPDF.csproj
```

## 编译与测试

```powershell
dotnet build KillerPDF.csproj -c Release
dotnet test KillerPDF.Tests\KillerPDF.Tests.csproj -c Release
```

## 发布单文件 EXE

```powershell
dotnet publish KillerPDF.csproj -c Release -r win-x64 --self-contained true
```

生成文件位于：

```text
bin\Release\net8.0-windows\win-x64\publish\KillerPDF.exe
```

项目已经在 `KillerPDF.csproj` 中启用单文件、自包含及原生库自动释放配置。也可以运行完整发布脚本：

```powershell
.\release.ps1
```

## 主要技术组件

- .NET 8 / WPF
- WPF UI
- PDFium / Docnet.Core
- PdfSharpCore / PDFsharp
- RapidOcrNet / PP-OCRv5 / ONNX Runtime
- Tesseract
- MarkdView
- WpfMath

## 与上游项目的关系

本项目基于 [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF) 修改，并保留了上游的大部分 PDF 编辑能力。在此基础上，本 Fork 主要增加和调整了：

- .NET 8 与单文件发布方案
- WPF UI 界面适配和中文字体优化
- PP-OCRv5 中文 OCR
- 多模型配置及 OpenAI 兼容流式对话
- PDF 截图 OCR 提问
- Markdown 与 LaTeX 公式渲染
- AI 总结生成 Markdown 笔记
- 本地历史对话保存与回放

感谢 KillerPDF 原作者及所有依赖项目的贡献。若需要原版功能说明、官方发行版或上游更新，请访问 [KillerPDF 上游仓库](https://github.com/SteveTheKiller/KillerPDF)。

## 许可证

本项目继承上游的 [GNU General Public License v3.0](LICENSE)。修改、分发或发布二进制版本时，需要继续遵守 GPLv3，并按许可证要求提供对应源代码和修改说明。
