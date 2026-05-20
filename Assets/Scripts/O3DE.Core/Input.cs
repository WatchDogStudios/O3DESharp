/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;

namespace O3DE
{
    /// <summary>
    /// Keyboard key codes. Values must match the mapping table in ScriptBindings.cpp KeyCodeToChannelId().
    /// </summary>
    public enum KeyCode
    {
        // Letters A-Z (0-25)
        A = 0, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

        // Digits 0-9 (26-35)
        Alpha0 = 26, Alpha1, Alpha2, Alpha3, Alpha4,
        Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,

        // Function keys F1-F12 (36-47)
        F1 = 36, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

        // Arrow keys (48-51)
        UpArrow = 48,
        DownArrow = 49,
        LeftArrow = 50,
        RightArrow = 51,

        // Modifier keys (52-57)
        LeftShift = 52,
        RightShift = 53,
        LeftControl = 54,
        RightControl = 55,
        LeftAlt = 56,
        RightAlt = 57,

        // Special keys (58-84)
        Space = 58,
        Return = 59,
        Escape = 60,
        Tab = 61,
        Backspace = 62,
        Delete = 63,
        Insert = 64,
        Home = 65,
        End = 66,
        PageUp = 67,
        PageDown = 68,
        CapsLock = 69,
        NumLock = 70,
        ScrollLock = 71,
        PrintScreen = 72,
        Pause = 73,

        // Punctuation (74-84)
        Minus = 74,
        Equals = 75,
        LeftBracket = 76,
        RightBracket = 77,
        Semicolon = 78,
        Apostrophe = 79,
        Comma = 80,
        Period = 81,
        Slash = 82,
        Backslash = 83,
        BackQuote = 84,
    }

    /// <summary>
    /// Mouse button identifiers.
    /// </summary>
    public enum MouseButton
    {
        Left = 0,
        Right = 1,
        Middle = 2,
    }

    /// <summary>
    /// Provides access to the O3DE input system from C#.
    ///
    /// Key state methods:
    /// - IsKeyDown: True while the key is held down (any frame).
    /// - IsKeyPressed: True only on the frame the key was first pressed.
    /// - IsKeyReleased: True only on the frame the key was released.
    ///
    /// Axis methods:
    /// - GetAxis("Horizontal"): Returns -1..1 based on A/D or Left/Right arrow keys.
    /// - GetAxis("Vertical"): Returns -1..1 based on W/S or Up/Down arrow keys.
    /// - GetAxis("MouseX") / GetAxis("MouseY"): Returns mouse movement delta.
    /// </summary>
    public static class Input
    {
        // ============================================================
        // Keyboard
        // ============================================================

        /// <summary>
        /// Returns true while the specified key is held down.
        /// </summary>
        public static bool IsKeyDown(KeyCode key)
        {
            unsafe { return InternalCalls.Input_IsKeyDown((int)key); }
        }

        /// <summary>
        /// Returns true during the frame the key was pressed down.
        /// </summary>
        public static bool IsKeyPressed(KeyCode key)
        {
            unsafe { return InternalCalls.Input_IsKeyPressed((int)key); }
        }

        /// <summary>
        /// Returns true during the frame the key was released.
        /// </summary>
        public static bool IsKeyReleased(KeyCode key)
        {
            unsafe { return InternalCalls.Input_IsKeyReleased((int)key); }
        }

        // ============================================================
        // Mouse Buttons
        // ============================================================

        /// <summary>
        /// Returns true while the specified mouse button is held down.
        /// </summary>
        public static bool IsMouseButtonDown(MouseButton button)
        {
            unsafe { return InternalCalls.Input_IsMouseButtonDown((int)button); }
        }

        /// <summary>
        /// Returns true during the frame the mouse button was pressed.
        /// </summary>
        public static bool IsMouseButtonPressed(MouseButton button)
        {
            unsafe { return InternalCalls.Input_IsMouseButtonPressed((int)button); }
        }

        /// <summary>
        /// Returns true during the frame the mouse button was released.
        /// </summary>
        public static bool IsMouseButtonReleased(MouseButton button)
        {
            unsafe { return InternalCalls.Input_IsMouseButtonReleased((int)button); }
        }

        // ============================================================
        // Mouse Position / Delta
        // ============================================================

        /// <summary>
        /// Gets the current normalized mouse position (0..1 range).
        /// X and Y are stored in the returned Vector3's X and Y fields.
        /// </summary>
        public static Vector3 MousePosition
        {
            get { unsafe { return InternalCalls.Input_GetMousePosition(); } }
        }

        /// <summary>
        /// Gets the mouse movement delta for this frame.
        /// X and Y deltas are stored in the returned Vector3's X and Y fields.
        /// </summary>
        public static Vector3 MouseDelta
        {
            get { unsafe { return InternalCalls.Input_GetMouseDelta(); } }
        }

        // ============================================================
        // Axis
        // ============================================================

        /// <summary>
        /// Gets the value of a named virtual axis.
        ///
        /// Built-in axes:
        /// - "Horizontal": A/D and Left/Right arrow keys (-1 to 1)
        /// - "Vertical": W/S and Up/Down arrow keys (-1 to 1)
        /// - "MouseX": Raw mouse X movement
        /// - "MouseY": Raw mouse Y movement
        /// </summary>
        /// <param name="axisName">The name of the axis</param>
        /// <returns>The axis value, typically in the range -1 to 1</returns>
        public static float GetAxis(string axisName)
        {
            unsafe { return InternalCalls.Input_GetAxis(axisName); }
        }
    }
}
