using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using MultiThreadedDownloaderLib;

namespace Fetch_server
{
    public partial class Form1 : Form
    {
        private Socket server = null;
        private bool active = false;
        private List<NativeSocket> clientList;
        private bool isClosed = false;

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            StartServer();
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            isClosed = true;
            StopServer(server);
        }

        private void btnStartServer_Click(object sender, EventArgs e)
        {
            StartServer();
        }

        private void btnStopServer_Click(object sender, EventArgs e)
        {
            StopServer(server);
        }

        private async void StartServer()
        {
            btnStartServer.Enabled = false;
            numericUpDownServerPort.Enabled = false;
            btnStopServer.Enabled = true;

            try
            {
                int serverPort = (int)numericUpDownServerPort.Value;
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, serverPort);
                server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.Bind(endPoint);
                server.Listen((int)SocketOptionName.MaxConnections);

                clientList = new List<NativeSocket>();

                LogEvent($"Server started on port {serverPort}");

                active = true;
                await Task.Run(() =>
                {
                    while (active)
                    {
                        try
                        {
                            Socket socket = server.Accept();
                            LogEvent($"{socket.RemoteEndPoint} is connected");
                            NativeSocket client = new NativeSocket(socket);

                            Task.Run(() =>
                            {
                                ProcessClient(client);
                                if (!client.IsDisposed)
                                {
                                    DisconnectClient(client);
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(ex.Message);
                            active = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                string msg = $"Ошибка запуска сервера! {ex.Message}";
                LogEvent(msg);

                if (server != null)
                {
                    server.Close();
                    server = null;
                }
                active = false;

                MessageBox.Show(msg, "Ошибка!",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);

                btnStartServer.Enabled = true;
                btnStopServer.Enabled = false;
                numericUpDownServerPort.Enabled = true;

                return;
            }

            server.Close();
            server = null;

            if (!isClosed)
            {
                btnStartServer.Enabled = true;
                btnStopServer.Enabled = false;
                numericUpDownServerPort.Enabled = true;

                LogEvent("Server stopped!");
            }
        }

        private void ProcessClient(NativeSocket client)
        {
            AddClient(client);

            byte[] buffer = new byte[ushort.MaxValue];
            try
            {
                int bytesRead = client.Handle.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                if (bytesRead == 0)
                {
                    LogEvent($"Zero bytes received from {client.Handle.RemoteEndPoint}");
                    return;
                }

                string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                System.Diagnostics.Debug.WriteLine(msg);
                string[] strings = msg.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                LogEvent($"{client.Handle.RemoteEndPoint} sent: {strings[0]}");

                string[] request = strings[0].Split(new char[] { ' ' }, 3);
                if (request.Length == 3)
                {
                    ProcessFetch(client, request[0], request[1], msg);
                }
                else
                {
                    SendMessage(client, GenerateResponse(400, "Client error", "Invalid request"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
                if (ex is SocketException)
                {
                    int socketErrorCode = (ex as SocketException).ErrorCode;
                    string t = $"Client read error! Socket error {socketErrorCode}";
                    System.Diagnostics.Debug.WriteLine(t);
                }
            }
        }

        private void SendMessage(NativeSocket client, string msg)
        {
            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            client.Handle.Send(msgBytes);
        }

        private void ProcessFetch(NativeSocket client, string method, string requestedUrl, string fullRequest)
        {
            if (method == "GET")
            {
                requestedUrl = HttpUtility.UrlDecode(requestedUrl);
                if (requestedUrl.StartsWith("/fetch?"))
                {
                    requestedUrl = requestedUrl.Substring(7);

                    FileDownloader d = new FileDownloader() { Url = requestedUrl };
                    int errorCode = d.DownloadString(out string response);
                    d.Dispose();
                    if (errorCode == 200)
                    {
                        SendMessage(client, GenerateResponse(errorCode, "OK", response));
                    }
                    else
                    {
                        SendMessage(client, GenerateResponse(errorCode, "Something went wrong", null));
                    }
                }
                else
                {
                    SendMessage(client, GenerateResponse(400, "Bad request", null));
                }
            }
            else
            {
                SendMessage(client, GenerateResponse(501, "Not implemented", null));
            }
        }

        private void DisconnectClient(NativeSocket client, bool autoRemove = true)
        {
            if (!client.IsDisposed)
            {
                LogEvent($"{client.Handle.RemoteEndPoint} is disconnected");
                if (autoRemove)
                {
                    RemoveClient(client);
                }
                client.Dispose();
            }
        }

        private void DisconnectAllClients()
        {
            if (clientList != null)
            {
                clientList.ForEach((client) =>
                {
                    DisconnectClient(client, false);
                });
                clientList.Clear();
            }
        }

        private void AddClient(NativeSocket client)
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)delegate { AddClient(client); });
            }
            else
            {
                lock (client)
                {
                    clientList.Add(client);
                }
            }
        }

        private void RemoveClient(NativeSocket client)
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)delegate { RemoveClient(client); });
            }
            else
            {
                lock (client)
                {
                    clientList.Remove(client);
                }
            }
        }

        private static string GenerateResponse(int errorCode, string msg, string body, long contentLength = 0L)
        {
            string t = $"HTTP/1.1 {errorCode} {msg}\r\n" +
                "Access-Control-Allow-Origin: *\r\n";
            if (!string.IsNullOrEmpty(body))
            {
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                t += "Content-Type: text/plain; charset=UTF-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n\r\n{body}";
            }
            else
            {
                t += $"Content-Length: {contentLength}\r\n\r\n";
            }
            return t;
        }

        private void LogEvent(string eventText)
        {
            if (!isClosed)
            {
                if (InvokeRequired)
                {
                    Invoke((MethodInvoker)delegate { LogEvent(eventText); });
                }
                else
                {
                    string dateTime = DateTime.Now.ToString("yyyy.MM.dd HH:mm:ss");
                    listBoxLog.Items.Add($"{dateTime}> {eventText}");
                    if (checkBoxAutoscroll.Checked)
                    {
                        listBoxLog.SelectedIndex = listBoxLog.Items.Count - 1;
                    }
                }
            }
        }

        private void StopServer(Socket serverSocket)
        {
            if (serverSocket != null)
            {
                try
                {
                    serverSocket.Shutdown(SocketShutdown.Both);
                    serverSocket.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    if (ex is SocketException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Socket error {(ex as SocketException).ErrorCode}");
                    }
                    serverSocket.Close();
                }

                DisconnectAllClients();
            }

            if (!isClosed)
            {
                btnStopServer.Enabled = false;
            }
        }
    }
}
