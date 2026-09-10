#if UNITY_EDITOR
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using PicoBridge.Network;
using PicoBridge.UI;
using UnityEditor;
using UnityEngine;

namespace PicoBridge.Editor
{
    public static class PicoBridgeAudioSmoke
    {
        public static void Run()
        {
            PicoBridgeSceneUiTemplate.RebuildPanelPrefab();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PicoBridge/PicoBridgePanel.prefab");
            if (prefab == null) throw new Exception("Panel prefab missing");
            var view = prefab.GetComponent<PicoBridgePanelView>();
            if (view.audioButton == null || view.microphoneMuteButton == null || view.audioStatusText == null)
                throw new Exception("Audio UI references were not serialized");

            // Real loopback I/O: idle longer than ReceiveTimeout, then peer EOF.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var go = new GameObject("AudioTcpSmoke");
            var client = go.AddComponent<PicoTcpClient>();
            Socket peer = null;
            try
            {
                client.autoReconnect = false;
                client.serverAddress = "127.0.0.1";
                client.serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                client.Connect();
                var accept = listener.AcceptSocketAsync();
                if (!accept.Wait(3000)) throw new Exception("TCP connect timed out");
                peer = accept.Result;
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (client.State != SocketState.Working && DateTime.UtcNow < deadline) Thread.Sleep(10);
                Thread.Sleep(4500);
                if (client.State != SocketState.Working) throw new Exception("Idle receive incorrectly closed TCP");
                // Consume CONNECT/VERSION so closing the server produces orderly EOF.
                var bytes = new byte[4096];
                while (peer.Available > 0) peer.Receive(bytes);
                peer.Shutdown(SocketShutdown.Both);
                peer.Close();
                peer = null;
                deadline = DateTime.UtcNow.AddSeconds(2);
                while (client.State == SocketState.Working && DateTime.UtcNow < deadline) Thread.Sleep(10);
                if (client.State == SocketState.Working) throw new Exception("Peer EOF was not detected");
            }
            finally
            {
                peer?.Close();
                client.Disconnect();
                listener.Stop();
                UnityEngine.Object.DestroyImmediate(go);
            }
            Debug.Log("[PicoAudioSmoke] PASS: serialized audio controls; idle timeout survives; peer EOF closes.");
        }
    }
}
#endif
