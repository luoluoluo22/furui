# 抖音搜索下载器

这个 Quicker 动作会先弹出一个本地界面，让你输入关键词、下载目录和下载数量；然后调用已加载的浏览器扩展执行抖音搜索导出，再由 Quicker 内置 C# 代码直接下载前 N 个视频到本地目录。

## 界面输入项

- `关键词`：搜索关键词，例如 `糖尿病`
- `下载目录`：视频保存目录，默认 `D:\chajian\downloads`
- `下载数量`：下载前几个视频，默认 `3`

## 默认配置

- 扩展 ID：默认自动检测，不再写死
- 默认下载目录：`D:\chajian\downloads`

## 执行流程

1. 弹出输入窗口并收集参数
2. 打开扩展触发页  
   `chrome-extension://<extension-id>/trigger.html?...`
3. 等待扩展在 `下载` 目录生成最新 JSON 导出文件
4. 从导出文件中提取 `videoId`、`detailUrl`、`videoUrl`
5. 使用 Quicker C# 直接发起视频下载

## 说明

- 这个动作依赖 Edge 中已经加载好的扩展
- 动作会自动从 Edge/Chrome 的 `Preferences` 中查找名为 `Douyin Keyword Search` 的扩展 ID
- 扩展负责搜索和导出
- Quicker 负责最后一步视频下载
- 如果导出的 JSON 因中文编码问题无法标准解析，动作会回退到正则提取模式
