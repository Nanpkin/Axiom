# 手动与自动亮度协调

2026-10-24：本批实现按降亮动作配置识别的短期偏好，连续画面相似度与长期学习仍是候选。

- 工具箱→滚轮调节亮度→工具设置：默认开启“与自动亮度协调”，偏好保留30分钟，可设0–240；0只影响当前一轮。旧式永久接管可选。
- 例：原100%自动降到50%，滚轮调至65%，本轮继续检测并保持65%；退出条件后仍渐变恢复原100%。再次进入相同动作配置且原基准相同，在保留期内沿用65%。普通画面手动调至80%时，下一轮原基准是80%，不硬设100%；既有日夜规则保持。
- 偏好只在首次应用/后续配置变化时匹配；时长届满不在仍活动的同一场景中抢回亮度。最多16组内存偏好，后台重启清空；不写配置或持续轮询。
- 手势进入现有恢复租约的原生工作线程，保留最初基线。退出条件取消在途手势并等待清理；范围不一致、外部值、未知及失败继续遵守既有恢复保护。无租约时走原有限手势，保留手动值。
- 当前按动作参数与原基准匹配，并非根据画面相似度识别；相同动作配置可共用偏好。锁屏后同配置偏好暂保留至到期/重启；锁屏失效、显式清除和长期记忆待后续确认，不冒充已实现。
- HDR/游戏门禁仍由用户任务设置决定。滑轮每条非零消息即时响应和高精度累计模式均保留；硬件响应速度与粒度不等同于手机显示面板。

## 研究依据

Android官方说明手动滑块可修正环境亮度偏好；AOSP将短期用户锚点、环境变化、失效与长期个性化分开。Axiom借鉴“手动修正不关闭自动”和“场景偏好不同于普通基准”，未复制环境光学习模型。Apple公开手动/自动亮度操作，未公开足够细节支持具体学习算法的断言。

- [Android官方：滑块与个性化](https://android-developers.googleblog.com/2018/11/getting-screen-brightness-right-for.html)
- [AOSP控制器](https://github.com/aosp-mirror/platform_frameworks_base/blob/master/services/core/java/com/android/server/display/AutomaticBrightnessController.java)
- [AOSP映射](https://github.com/aosp-mirror/platform_frameworks_base/blob/master/services/core/java/com/android/server/display/BrightnessMappingStrategy.java)
- [Apple系统亮度说明](https://support.apple.com/en-us/109351)
