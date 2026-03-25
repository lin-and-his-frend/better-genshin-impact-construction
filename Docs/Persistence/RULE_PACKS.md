# 规则包与黑白名单设计

## 设计结论

黑白名单正文不入数据库。

原因：

1. 公共规则未来可能在线更新，文件化更容易分发与替换。
2. 用户需要手工修改与合并规则。
3. 规则正文更接近文本资源，而不是结构化业务记录。

## 三层规则结构

### 1. 内置基线规则

位置：
- `Assets\Config\Pick\default_pick_black_lists.json`

### 2. 在线公共规则包

位置：
- `User\ResourcePacks\Pick\public_blacklist.json`

### 3. 用户本地覆盖

位置：
- `User\Rules\Pick\blacklist.txt`
- `User\Rules\Pick\fuzzy_blacklist.txt`
- `User\Rules\Pick\whitelist.txt`

## 生效顺序

1. 内置基线黑名单
2. 在线公共黑名单包
3. 用户精确黑名单
4. 用户模糊黑名单
5. 用户白名单提权

## 数据库中允许保存的内容

- 公共规则包版本号
- 来源 URL / 渠道
- 校验哈希
- 更新时间

## 废弃项

- `User\pick_black_lists.json`
- `User\pick_white_lists.json`

迁移后不再作为真源读取。
