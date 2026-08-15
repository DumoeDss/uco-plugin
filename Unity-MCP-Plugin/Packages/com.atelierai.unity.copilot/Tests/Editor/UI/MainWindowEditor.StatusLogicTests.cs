/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using com.AtelierAI.Unity.Copilot.Editor.Services;
using com.AtelierAI.Unity.Copilot.Editor.UI;
using com.IvanMurzak.McpPlugin;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    public class MainWindowEditorStatusLogicTests
    {
        #region GetConnectionStatusClass

        [TestCase(ConnectionState.Connected, true, MainWindowEditor.USS_Connected)]
        [TestCase(ConnectionState.Connected, false, MainWindowEditor.USS_Disconnected)]
        [TestCase(ConnectionState.Disconnected, true, MainWindowEditor.USS_Connecting)]
        [TestCase(ConnectionState.Disconnected, false, MainWindowEditor.USS_Disconnected)]
        [TestCase(ConnectionState.Reconnecting, true, MainWindowEditor.USS_Connecting)]
        [TestCase(ConnectionState.Reconnecting, false, MainWindowEditor.USS_Disconnected)]
        public void GetConnectionStatusClass(ConnectionState state, bool keepConnected, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetConnectionStatusClass(state, keepConnected));
        }

        #endregion

        #region GetConnectionStatusText

        [TestCase(ConnectionState.Connected, true, "Connected")]
        [TestCase(ConnectionState.Connected, false, "Disconnected")]
        [TestCase(ConnectionState.Disconnected, true, "Connecting...")]
        [TestCase(ConnectionState.Disconnected, false, "Disconnected")]
        [TestCase(ConnectionState.Reconnecting, true, "Connecting...")]
        [TestCase(ConnectionState.Reconnecting, false, "Disconnected")]
        public void GetConnectionStatusText(ConnectionState state, bool keepConnected, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetConnectionStatusText(state, keepConnected));
        }

        #endregion

        #region GetButtonText

        [TestCase(ConnectionState.Connected, true, "Disconnect")]
        [TestCase(ConnectionState.Connected, false, "Connect")]
        [TestCase(ConnectionState.Disconnected, true, "Stop")]
        [TestCase(ConnectionState.Disconnected, false, "Connect")]
        [TestCase(ConnectionState.Reconnecting, true, "Stop")]
        [TestCase(ConnectionState.Reconnecting, false, "Connect")]
        public void GetButtonText(ConnectionState state, bool keepConnected, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetButtonText(state, keepConnected));
        }

        #endregion

        #region GetServerButtonText

        [TestCase(CopilotServerStatus.Running, "Stop")]
        [TestCase(CopilotServerStatus.Starting, "Starting...")]
        [TestCase(CopilotServerStatus.Stopping, "Stopping...")]
        [TestCase(CopilotServerStatus.Stopped, "Start")]
        [TestCase(CopilotServerStatus.External, "External")]
        public void GetServerButtonText(CopilotServerStatus status, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetServerButtonText(status));
        }

        #endregion

        #region GetServerStatusClass

        [TestCase(CopilotServerStatus.Running, MainWindowEditor.USS_Connected)]
        [TestCase(CopilotServerStatus.Starting, MainWindowEditor.USS_Connecting)]
        [TestCase(CopilotServerStatus.Stopping, MainWindowEditor.USS_Connecting)]
        [TestCase(CopilotServerStatus.Stopped, MainWindowEditor.USS_Disconnected)]
        [TestCase(CopilotServerStatus.External, MainWindowEditor.USS_External)]
        public void GetServerStatusClass(CopilotServerStatus status, string expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.GetServerStatusClass(status));
        }

        #endregion

        #region IsServerButtonEnabled

        [TestCase(CopilotServerStatus.Running, true)]
        [TestCase(CopilotServerStatus.Starting, false)]
        [TestCase(CopilotServerStatus.Stopping, false)]
        [TestCase(CopilotServerStatus.Stopped, true)]
        [TestCase(CopilotServerStatus.External, false)]
        public void IsServerButtonEnabled(CopilotServerStatus status, bool expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.IsServerButtonEnabled(status));
        }

        #endregion

        #region CombineCopilotServerStatus

        [TestCase(CopilotServerStatus.Stopped, true, CopilotServerStatus.External)]
        [TestCase(CopilotServerStatus.Starting, true, CopilotServerStatus.External)]
        [TestCase(CopilotServerStatus.Running, true, CopilotServerStatus.Running)]
        [TestCase(CopilotServerStatus.Stopped, false, CopilotServerStatus.Stopped)]
        [TestCase(CopilotServerStatus.Running, false, CopilotServerStatus.Running)]
        [TestCase(CopilotServerStatus.Stopping, true, CopilotServerStatus.External)]
        [TestCase(CopilotServerStatus.Stopping, false, CopilotServerStatus.Stopping)]
        public void CombineCopilotServerStatus(CopilotServerStatus status, bool isConnected, CopilotServerStatus expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.CombineCopilotServerStatus(status, isConnected));
        }

        #endregion

        #region GetServerLabelText

        [Test]
        public void GetServerLabelText_Running()
        {
            Assert.AreEqual("Server: Running", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Running));
        }

        [Test]
        public void GetServerLabelText_Starting()
        {
            Assert.AreEqual("Server: Starting...", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Starting));
        }

        [Test]
        public void GetServerLabelText_Stopping()
        {
            Assert.AreEqual("Server: Stopping...", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Stopping));
        }

        [Test]
        public void GetServerLabelText_Stopped()
        {
            Assert.AreEqual("Server", MainWindowEditor.GetServerLabelText(CopilotServerStatus.Stopped));
        }

        [Test]
        public void GetServerLabelText_External()
        {
            Assert.AreEqual("Server: External", MainWindowEditor.GetServerLabelText(CopilotServerStatus.External));
        }

        #endregion

        #region ComputeCloudAuthState

        [Test]
        public void ComputeCloudAuthState_Cloud_NullToken()
        {
            var (needsAuth, hasToken, isCloud) = MainWindowEditor.ComputeCloudAuthState(ConnectionMode.Cloud, null);
            Assert.IsTrue(needsAuth);
            Assert.IsFalse(hasToken);
            Assert.IsTrue(isCloud);
        }

        [Test]
        public void ComputeCloudAuthState_Cloud_EmptyToken()
        {
            var (needsAuth, hasToken, isCloud) = MainWindowEditor.ComputeCloudAuthState(ConnectionMode.Cloud, "");
            Assert.IsTrue(needsAuth);
            Assert.IsFalse(hasToken);
            Assert.IsTrue(isCloud);
        }

        [Test]
        public void ComputeCloudAuthState_Cloud_ValidToken()
        {
            var (needsAuth, hasToken, isCloud) = MainWindowEditor.ComputeCloudAuthState(ConnectionMode.Cloud, "abc");
            Assert.IsFalse(needsAuth);
            Assert.IsTrue(hasToken);
            Assert.IsTrue(isCloud);
        }

        [Test]
        public void ComputeCloudAuthState_Custom_NullToken()
        {
            var (needsAuth, hasToken, isCloud) = MainWindowEditor.ComputeCloudAuthState(ConnectionMode.Custom, null);
            Assert.IsFalse(needsAuth);
            Assert.IsFalse(hasToken);
            Assert.IsFalse(isCloud);
        }

        [Test]
        public void ComputeCloudAuthState_Custom_ValidToken()
        {
            var (needsAuth, hasToken, isCloud) = MainWindowEditor.ComputeCloudAuthState(ConnectionMode.Custom, "abc");
            Assert.IsFalse(needsAuth);
            Assert.IsTrue(hasToken);
            Assert.IsFalse(isCloud);
        }

        #endregion

        #region IsAuthFlowRunning

        [TestCase(DeviceAuthFlowState.Initiating, true)]
        [TestCase(DeviceAuthFlowState.WaitingForUser, true)]
        [TestCase(DeviceAuthFlowState.Polling, true)]
        [TestCase(DeviceAuthFlowState.Idle, false)]
        [TestCase(DeviceAuthFlowState.Authorized, false)]
        [TestCase(DeviceAuthFlowState.Failed, false)]
        [TestCase(DeviceAuthFlowState.Expired, false)]
        [TestCase(DeviceAuthFlowState.Cancelled, false)]
        public void IsAuthFlowRunning(DeviceAuthFlowState state, bool expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.IsAuthFlowRunning(state));
        }

        #endregion

        #region GetAuthFlowStatusMessage

        [Test]
        public void GetAuthFlowStatusMessage_Initiating()
        {
            Assert.AreEqual("Initiating...", MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Initiating, null, null));
        }

        [Test]
        public void GetAuthFlowStatusMessage_WaitingForUser()
        {
            var result = MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.WaitingForUser, "ABC123", null);
            Assert.AreEqual("Code: ABC123 — Authorize in browser", result);
        }

        [Test]
        public void GetAuthFlowStatusMessage_Polling()
        {
            var result = MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Polling, "XYZ789", null);
            Assert.AreEqual("Code: XYZ789 — Waiting for authorization...", result);
        }

        [Test]
        public void GetAuthFlowStatusMessage_Authorized()
        {
            Assert.AreEqual("Authorized!", MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Authorized, null, null));
        }

        [Test]
        public void GetAuthFlowStatusMessage_Failed_WithMessage()
        {
            var result = MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Failed, null, "timeout");
            Assert.AreEqual("Failed: timeout", result);
        }

        [Test]
        public void GetAuthFlowStatusMessage_Failed_NullMessage()
        {
            var result = MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Failed, null, null);
            Assert.AreEqual("Failed: ", result);
        }

        [Test]
        public void GetAuthFlowStatusMessage_Expired()
        {
            Assert.AreEqual("Expired — try again", MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Expired, null, null));
        }

        [Test]
        public void GetAuthFlowStatusMessage_Cancelled()
        {
            Assert.AreEqual("Cancelled", MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Cancelled, null, null));
        }

        [Test]
        public void GetAuthFlowStatusMessage_Idle()
        {
            Assert.AreEqual("", MainWindowEditor.GetAuthFlowStatusMessage(DeviceAuthFlowState.Idle, null, null));
        }

        #endregion

        #region IsHostFieldReadOnly

        [TestCase(true, ConnectionState.Disconnected, true)]
        [TestCase(true, ConnectionState.Connected, true)]
        [TestCase(true, ConnectionState.Reconnecting, true)]
        [TestCase(false, ConnectionState.Disconnected, false)]
        [TestCase(false, ConnectionState.Connected, true)]
        [TestCase(false, ConnectionState.Reconnecting, true)]
        public void IsHostFieldReadOnly(bool keepConnected, ConnectionState state, bool expected)
        {
            Assert.AreEqual(expected, MainWindowEditor.IsHostFieldReadOnly(keepConnected, state));
        }

        #endregion
    }
}
