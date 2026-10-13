using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VtInputHandler 的依赖倒置接口（ADR-0009 / ADR-0010）。
    /// AgentCliProtocol 实现它，内部把 scroll 成员（ScrollOffset/DispatchWheel/DispatchScroll）委托给 ScrollController。
    /// ADR-0010：移除 GameConsole 属性，改为直接暴露 PressPrimitiveKey/InputMouseKey 原语分发方法，
    /// 让 VtInputHandler 不再依赖 EmueraConsole 类型，fake host 可用 spy 计数器测试 primitive 路径。
    /// </summary>
    internal interface IVtHost
    {
        /// <summary>请求退出（Ctrl+C / CancelKeyPress）。</summary>
        void RequestExit();

        /// <summary>是否在 primitive 输入等待状态（TINPUT #xxx）。门卫读取，false 时绕过 primitive 路径。</summary>
        bool IsWaitingPrimitive { get; }

        /// <summary>当前 Scroll Offset（VtInputHandler 门卫读取）。</summary>
        int ScrollOffset { get; }

        /// <summary>滚轮事件分发（cb=64/65 → delta=±3）。</summary>
        void DispatchWheel(int delta);

        /// <summary>键盘滚动热键分发（PgUp/PgDn/Home/End）。</summary>
        void DispatchScroll(ScrollAction action);

        /// <summary>VT 解析器输出的 ConsoleKeyInfo 入口。</summary>
        void ProcessKeyFromVt(ConsoleKeyInfo key);

        /// <summary>
        /// 鼠标点击命中按钮的分发（Phase 3-3 / ADR-0014 value-based）。
        /// value 是按钮提交值（int/long 装箱或 string），isInteger 标记整数按钮——
        /// 由 ButtonRegionTracker.HitTest 返回的 Region 直接传值，不再依赖 ConsoleButtonString 引用。
        /// 服务端 ConsoleInputHandler 按 button.Generation 校验兜底。
        /// </summary>
        void DispatchMouseClick(object value, bool isInteger);

        /// <summary>鼠标点击未命中的分发（推进 AnyKey/EnterKey）。</summary>
        void DispatchMouseMiss();

        /// <summary>
        /// ADR-0031 / spec-v3 M4：鼠标位置上报（VT 终端行列，0-based）。
        /// 与点击命中路径（<see cref="DispatchMouseClick"/>）<b>正交</b> —— 只回答「鼠标在哪」，
        /// 不提交任何值、不推进任何等待态。无 Geometry 能力的宿主实现为空操作。
        /// </summary>
        void SubmitPointerGeometry(int row, int col);

        /// <summary>primitive 键盘输入分发（IsWaitingPrimitive=true 时由 OnKeyEvent 调用）。</summary>
        void PressPrimitiveKey(int keycode, int keydata, int keymod);

        /// <summary>primitive 鼠标输入分发（IsWaitingPrimitive=true 时由 OnMouseEvent 调用）。</summary>
        void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5);
    }
}
