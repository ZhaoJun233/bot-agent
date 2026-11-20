# UI 契约 1.0.0

PluginUiBridge 是 D1 同进程 Vue 页面组合接口。只公开类型与调用约定，不包含宿主实现。组件需绑定同一个 Vue 运行时；JavaScript 插件不是安全沙箱。

- ManagementDialog 采用 UiDialogProps，默认插槽放页面，eyebrow 插槽可放上标。close 请求仍交页面判断草稿与未决提交。
- ErrorNotice 采用 UiErrorNoticeProps，仅显示安全错误码与文案。
- PluginSwitch 采用 UiPluginSwitchProps，description 插槽提供 runtime 状态文字，busy/saved/auth-expired 事件通知页面。身份、权限与插件 ID 由宿主适配器绑定。
- operations 由宿主预先绑定身份与操作类型。list 必须可响应式追踪、保留稳定未变记录引用并返回不可变投影。begin 检查当前所有权和权限；mark 只能处理同种操作的原键，允许卸载或锁定后的原作用域晚到回执完成登记。不得跨身份重建请求或自动重放。
- requestError 只把真实传输错误投影为 status/code；toUiError 提供安全文案。newOperationKey 产生原请求键。protectDraft 在组件 setup 中调用，由宿主绑定和清理离页提示。

runtime UI 的资源摘要、消息协议、版本错配拒写、激活代次与旧 UI 续存由 D2 另行完成；当前契约不承担这些尚未实现的保证。
