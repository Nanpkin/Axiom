# 手动与自动亮度协调候选

2026-10-24，研究方案，尚未改变运行策略。用户要求先查Android/iOS，不接受简单“退出条件就重置”的策略。

Android：手动滑块修改当前环境光对应的亮度偏好，自动亮度继续工作。AOSP短期模型保存环境锚点和手动亮度，闲置失效后还结合环境变化决定重置；长期个性化与短期修正分开。变化经过滞回、稳定期限与渐变。官方并未保证所有厂商共用参数。

iOS：Apple确认环境光驱动自动亮度，手动滑块可调；没有公布足够信息证明具体学习曲线或手动修正失效算法。UIScreen应用改亮度至锁屏的规则不能当成控制中心调亮度的系统策略。

## Axiom建议

- 分开存储：常用基准（通常100%、可配置且尊重既有时段规则）、场景调暗目标、当前实际读回、手动场景修正。自动写入与失败残值永不成为基准。
- 手动输入立即优先，但不中断检测或永久退出自动。已在亮页面调暗50%时调到65%，表示这类画面下临时更喜欢65%，不把普通桌面基准改成65%。
- 短期修正锚定画面亮区评分/集中程度与既有时段；附近波动保持修正，不立刻拉回。持续离开相应场景后平滑回当前有效常用基准；普通场景主动调低也保留短期意图，不无条件抢回100%。
- 短期修正结束依据场景显著变化、锁屏/熄屏或显式恢复，期限和滞回可配置；时间到不应在用户仍读同类页面时突然重置。具体阈值尚未确定。
- 长期记忆先默认关闭，只有显式“记住此场景”或后续经确认的重复偏好才持久化。采用小型确定性场景表，不加入运行时AI或训练服务。
- HDR/游戏门禁仍由任务设置决定；未知保持解释状态。最终需要单一亮度协调者串行执行，不能让手动命令、自动租约及恢复线程分别争写；现有失败租约保护必须保留。

## 依据

- [Android官方：滑块与个性化](https://android-developers.googleblog.com/2018/11/getting-screen-brightness-right-for.html)
- [AOSP控制器：短期模型、环境滞回与渐变](https://github.com/aosp-mirror/platform_frameworks_base/blob/master/services/core/java/com/android/server/display/AutomaticBrightnessController.java)
- [AOSP映射：手动锚点与曲线](https://github.com/aosp-mirror/platform_frameworks_base/blob/master/services/core/java/com/android/server/display/BrightnessMappingStrategy.java)
- [Apple系统亮度说明](https://support.apple.com/en-us/109351)
