# WebUI Library 搜索和分页优化（2026-03-04）

## 优化概述

为 WebUI 的 Library 区域（JS 脚本、键鼠录制、路径追踪）添加搜索功能，并将分页大小统一调整为每页 5 条。

## 修改内容

### 1. 更新 state 初始化

**文件位置**: `BetterGenshinImpact/Service/Remote/WebRemoteIndex.html` (约 5479 行)

**修改前**:
```javascript
libraryView: {
  js: { items: [], page: 1, pageSize: 12 },
  pathing: { items: [], page: 1, pageSize: 12 },
  keymouse: { items: [], status: "Unknown", page: 1, pageSize: 12 }
},
```

**修改后**:
```javascript
libraryView: {
  js: { items: [], page: 1, pageSize: 5, filter: "" },
  pathing: { items: [], page: 1, pageSize: 5, filter: "" },
  keymouse: { items: [], status: "Unknown", page: 1, pageSize: 5, filter: "" }
},
```

**变更说明**:
- 将 `pageSize` 从 12 改为 5
- 为每个 library 类型添加 `filter` 字段用于存储搜索关键词

### 2. 优化 renderLibrarySection 函数

**文件位置**: `BetterGenshinImpact/Service/Remote/WebRemoteIndex.html` (约 9768 行)

**新增功能**:

1. **添加搜索输入框**
   ```javascript
   const filterInput = create("input");
   filterInput.placeholder = t("搜索名称或路径");
   filterInput.style.minWidth = "180px";
   filterInput.value = view.filter || "";
   filterInput.setAttribute("aria-label", t("搜索名称或路径"));
   tools.appendChild(filterInput);
   ```

2. **绑定搜索事件**
   ```javascript
   bindSearchInput(filterInput, (value) => {
     view.filter = value;
     view.page = 1;
     draw();
   });
   ```

3. **实现搜索过滤逻辑**
   ```javascript
   const keyword = String(view.filter || "").trim().toLowerCase();
   const rawItems = Array.isArray(view.items) ? view.items : [];
   const filteredItems = keyword
     ? rawItems.filter(item => {
       const haystack = `${item.name || ""} ${item.folder || ""} ${item.relativePath || ""}`.toLowerCase();
       return haystack.includes(keyword);
     })
     : rawItems;
   ```

4. **更新空状态提示**
   ```javascript
   if (!pageInfo.data.length) {
     list.appendChild(create("div", "placeholder", keyword ? "无匹配结果" : "暂无数据"));
   }
   ```

## 功能特性

### 搜索功能
- ✅ 支持实时搜索（输入时自动过滤）
- ✅ 搜索范围包括：名称、文件夹、相对路径
- ✅ 不区分大小写
- ✅ 支持中文输入法（使用 compositionstart/compositionend 事件）
- ✅ 搜索时自动重置到第一页
- ✅ 无匹配结果时显示友好提示

### 分页功能
- ✅ 每页显示 5 条记录
- ✅ 支持上一页/下一页导航
- ✅ 显示当前页码和总页数
- ✅ 搜索过滤后的结果也支持分页

### 适用范围
- ✅ JS 脚本库 (kind: "js")
- ✅ 键鼠录制库 (kind: "keymouse")
- ✅ 路径追踪库 (kind: "pathing")

## 其他已有搜索和分页的区域

以下区域在本次修改前已经具备搜索和分页功能，无需修改：

1. **调度器 - 配置组列表**
   - 搜索字段：配置组名称
   - 分页大小：5 条/页
   - 位置：`drawSchedulerGroups` 函数

2. **调度器 - 组详情（项目列表）**
   - 搜索字段：任务名称、类型、目录
   - 分页大小：5 条/页
   - 位置：`drawSchedulerDetail` 函数

3. **调度器 - 脚本库选择器**
   - 搜索字段：名称、文件夹、类型
   - 分页大小：5 条/页
   - 位置：`renderLibraryList` 函数（在 renderSchedulerSection 内）

4. **一条龙 - 配置字段**
   - 搜索字段：字段名称
   - 分页大小：可选 5 或全部
   - 位置：`renderOneDragonSection` 函数

5. **一条龙 - 任务列表**
   - 搜索字段：任务名称
   - 分页大小：可选 5 或全部
   - 位置：`renderOneDragonSection` 函数

6. **配置组 (config_group)**
   - 搜索字段：字段路径或名称
   - 分页大小：可选 10/20/35/50
   - 位置：`renderConfigGroupSection` 函数

## 用户体验改进

### 视觉一致性
- 搜索输入框样式与其他页面保持一致
- 使用统一的占位符文本格式
- 空状态提示清晰明确

### 交互优化
- 搜索时自动重置到第一页，避免用户困惑
- 支持中文输入法，输入完成后才触发搜索
- 分页控件位置固定，便于快速导航

### 性能考虑
- 搜索过滤在客户端进行，响应迅速
- 使用防抖机制避免频繁渲染
- 分页减少 DOM 节点数量，提升渲染性能

## 兼容性

- ✅ 向后兼容，不影响现有功能
- ✅ 搜索关键词持久化在 state 中
- ✅ 页码状态在切换页面后保持

## 测试建议

1. **基础功能测试**
   - 在 JS 脚本库中输入搜索关键词，验证过滤结果
   - 在键鼠录制库中测试搜索功能
   - 在路径追踪库中测试搜索功能

2. **边界情况测试**
   - 搜索不存在的关键词，验证"无匹配结果"提示
   - 清空搜索框，验证显示全部结果
   - 在搜索结果中翻页，验证分页正常工作

3. **中文输入测试**
   - 使用中文输入法输入关键词
   - 验证输入过程中不会触发搜索
   - 验证输入完成后正确触发搜索

4. **性能测试**
   - 在包含大量项目的库中测试搜索性能
   - 验证分页加载速度

## 总结

本次优化为 Library 区域添加了完整的搜索和分页功能，使其与调度器、一条龙等其他区域保持一致的用户体验。每页显示 5 条记录的设计既保证了信息密度，又避免了过度滚动，提升了整体可用性。
