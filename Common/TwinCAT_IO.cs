﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwinCAT;
using TwinCAT.Ads;

namespace Common
{
    public class TcIO
    {
        //global members
        TcAdsClient TcClient = null;
        TCSMessageEventArgs LogArgs;
        TCSStatusUpdateEventArgs StatusArgs;

        /// <summary>
        /// Default constructor. Instantiates TcAdsClient and logging arguments and status update arguments
        /// </summary>
        public TcIO()
        {
            TcClient = new TcAdsClient();
            LogArgs = new TCSMessageEventArgs();
            LogArgs.Recipient = Recipient.TwinCatTextBox;
            LogArgs.Sender = this;
            StatusArgs = new TCSStatusUpdateEventArgs();
            StatusArgs.Recipient = Recipient.TwinCatTextBox;
            StatusArgs.Sender = this;
        }

        /// <summary>
        /// Connects to TwinCAT Ads via TcAdsClient.Connect. Hooks up Ads events to logging text box
        /// </summary>
        /// <param name="amsNetId">As defined in TwinCAT Project (in Project > System > Routes > Project Routes). Something like 192.168.0.1.1.1 </param>
        /// <param name="port">As defined in TwinCAT. Normally 851 or 852</param>
        public void Connect(string amsNetId, int port)
        {
            try
            {
                if (TcClient == null)
                    TcClient = new TcAdsClient();

                TcClient.ConnectionStateChanged += TcClient_ConnectionStateChanged;
                TcClient.AdsNotification += TcClient_AdsNotification;
                TcClient.AdsNotificationError += TcClient_AdsNotificationError;
                TcClient.AdsNotificationEx += TcClient_AdsNotificationEx;
                TcClient.AdsStateChanged += TcClient_AdsStateChanged;
                TcClient.AdsSymbolVersionChanged += TcClient_AdsSymbolVersionChanged;
                TcClient.AmsRouterNotification += TcClient_AmsRouterNotification;

                AmsNetId id = new AmsNetId(amsNetId);
                TcClient.Connect(id, port);
            }
            catch (Exception e)
            {
                Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Error, string.Format("Could not connect to ADS Server: {0}", e));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns>Instance is valid and connected to TwinCAT</returns>
        public bool IsRunning()
        {
            return TcClient != null && TcClient.IsConnected;
        }

        /// <summary>
        /// Disconnects client and disposes all resources affiliate with it
        /// </summary>
        public void Disconnect()
        {
            TcClient?.Disconnect();
            TcClient?.Dispose();
            TcClient = null;
        }

        #region notification stuff

        private void TcClient_AmsRouterNotification(object sender, AmsRouterNotificationEventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Info, string.Format("AMS Router notification {0}", e.State), Verbosity.Verbose);
        }

        private void TcClient_AdsSymbolVersionChanged(object sender, EventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Info, string.Format("Ads Symbol Version changed."), Verbosity.Verbose);
        }

        private void TcClient_AdsStateChanged(object sender, AdsStateChangedEventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Info, string.Format("Ads State changed to{0}AdsState: {1}{0}Device State: {2}", Logging.NewLineTab, e.State.AdsState, e.State.DeviceState));
        }

        private void TcClient_AdsNotificationEx(object sender, AdsNotificationExEventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Info,
                string.Format("Received Extended Ads Notification: {0}", e.ToString()));
        }

        private void TcClient_AdsNotificationError(object sender, AdsNotificationErrorEventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Error,
                string.Format("Ads Notification Error: {0}", e.ToString()));
        }

        private void TcClient_AdsNotification(object sender, AdsNotificationEventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Info,
                string.Format("Received Ads Notification: {0}", e.ToString()));
        }

        private void TcClient_ConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Info,
                string.Format("TwinCAT Client Connection State Change from {0} to {1}. Reason: {2}", e.OldState, e.NewState, e.Reason));
        }

        private void Send_TcClient_EventHandling(DateTime when, LogTextCategory category, string message, Verbosity verbosity = Verbosity.Important)
        {
            LogArgs.When = when;
            LogArgs.Category = category;
            LogArgs.Message = message;
            LogArgs.Verbosity = verbosity;
            Logging.SendMessage(LogArgs);
            StatusArgs.IsAlive = TcClient != null && TcClient.IsConnected;
            Logging.UpdateStatus(StatusArgs);
        }
        #endregion

        /// <summary>
        /// Processes incoming requests of request_type 'read' or 'write'
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Same as incoming request but with fields for 'values' (in case of read request) and / or 'message' (in case of error) filled.</returns>
        public TCRequest ProcessTCRequest(TCRequest request)
        {
            var hash = request.GetHashCode();
            string currentVar = "";
            try
            {
                if (request.names?.Length != request.types?.Length || 
                    request.names?.Length == 0 ||
                    request.request_type == null ||                     
                    (request.request_type != "read" && request.request_type != "write") ||
                    (request.request_type == "write" && request.values?.Length != request.names?.Length))
                {
                    request.message = "Invalid request! Length of names and types must be equal and larger than zero. Request type must be either 'read' or 'write'. If request_type is 'write' values must be supplied.";
                    Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Error, request.message, Verbosity.Important);
                    return request;
                }


                Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Incoming, string.Format("Received ADS {0} request for {1} items (hash: {2})", request.request_type, request.names.Length, hash));

                //read request
                if (request.request_type == "read")
                {
                    request.values = new object[request.names.Length];
                    for (int i = 0; i < request.names.Length; i++)
                    {
                        currentVar = request.names[i];
                        if (!request.types[i].EndsWith("]") || request.types[i].StartsWith("string"))
                            request.values[i] = ReadItem(request.names[i], request.types[i]);
                        else
                        {
                            var length = int.Parse(request.types[i].Split('[')[1].Split(']')[0]);
                            request.values[i] = ReadArray(request.names[i], request.types[i], length);
                        }
                    }
                }

                //write request
                else
                {
                    for (int i = 0; i < request.names.Length; i++)
                    {
                        currentVar = request.names[i];
                        if (!request.types[i].EndsWith("]") || request.types[i].StartsWith("string"))
                            WriteItem(request.names[i], request.types[i], request.values[i]);
                        else
                        {
                            var length = int.Parse(request.types[i].Split('[')[1].Split(']')[0]);
                            var arr = (request.values[i] as Newtonsoft.Json.Linq.JArray).ToObject<object[]>();
                            if (arr.Length != length)
                                throw new Exception(
                                    string.Format(
                                        "Write request for {0}: Array length in 'values' ({1}) doesn't match the indicated array length in 'types' property ({2}). " +
                                        "Make sure they are the same\nFor example if your item is an integer array of length three ('values' = [[2, 4, 1]]) " +
                                        "make sure to indicate the correct length in 'types' = ['int[3]']", request.names[i], arr.Length, length));

                            WriteArray(request.names[i], request.types[i], arr);
                        }
                    }
                }


                Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Outgoing, string.Format("Responded to ADS {0} request for {1} items (hash: {2})", request.request_type, request.names.Length, hash));
            }
            catch (Exception e)
            {
                request.message = string.Format("Error at variable: '{0}'. --- Error Message: {1}. --- Please refer to TwinCATAdsServer log for more info.", currentVar, e.Message);
                Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Error,
                    string.Format("Exception occured during processing of request:{0}Variable: {1}{0}Request hash: {2}{0}Exception: {3}", Logging.NewLineTab, currentVar, hash, e.ToString()));
            }
            return request;
        }

        /// <summary>
        /// Read singular item from TwinCAT variables
        /// </summary>
        /// <param name="name"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public object ReadItem(string name, string type)
        {
            return ReadArray(name, type, 1)[0];
        }

        /// <summary>
        /// Read item array from TwinCAT Variables
        /// </summary>
        /// <param name="name"></param>
        /// <param name="type"></param>
        /// <param name="arrLength"></param>
        /// <returns></returns>
        public object[] ReadArray(string name, string type, int arrLength)
        {
            object[] values = new object[arrLength];

            int streamLength = StreamLength(type);

            using (AdsStream dataStream = new AdsStream(arrLength * streamLength))
            using (AdsBinaryReader reader = new AdsBinaryReader(dataStream))
            {
                int varHandle = TcClient.CreateVariableHandle(name);
                TcClient.Read(varHandle, dataStream);
                dataStream.Position = 0;

                for (int i = 0; i < arrLength; i++)
                {
                    values[i] = ReadObjectFromReader(reader, type);
                }

                TcClient.DeleteVariableHandle(varHandle);
            }

            return values;
        }

        /// <summary>
        /// Write singular item to TwinCAT Variables
        /// </summary>
        /// <param name="name"></param>
        /// <param name="type"></param>
        /// <param name="value"></param>
        public void WriteItem(string name, string type, object value)
        {
            WriteArray(name, type, new object[1] { value });
        }

        /// <summary>
        /// Write item array to TwinCAT Variables
        /// </summary>
        /// <param name="name"></param>
        /// <param name="type"></param>
        /// <param name="values"></param>
        public void WriteArray(string name, string type, object[] values)
        {
            int arrLength = values.Length;
            int streamLength = StreamLength(type);

            using (AdsStream dataStream = new AdsStream(arrLength * streamLength))
            using (AdsBinaryWriter writer = new AdsBinaryWriter(dataStream))
            {
                int varHandle = TcClient.CreateVariableHandle(name);
                dataStream.Position = 0;

                foreach (object val in values)
                {
                    WriteObjectToWriter(writer, type, val);
                }
                TcClient.Write(varHandle, dataStream);
                writer.Flush();
            }
        }

        /*
        public static string ReadStruct(int varHandle, List<string> types, int[] size, ref TcAdsClient tcClient, out List<List<object>> output)
        {
            output = new List<List<object>>();
            try
            {
                string message = "";
                int streamLength = 0;
                for (int i = 0; i < size.Length; i++) streamLength += StreamLength(types[i]) * size[i];

                AdsStream dataStream = new AdsStream(streamLength);
                AdsBinaryReader reader = new AdsBinaryReader(dataStream);
                for (int i = 0; i < size.Length; i++)
                {
                    List<object> o = new List<object>();
                    for (int j = 0; j < size[i]; j++)
                    {
                        object obj = new object();
                        if (!ReadObject(reader, types[i], out obj))
                            message = String.Format("Error while reading " + types[i] + " at struct position (i, j): (" + i + ", " + j + ")");
                        o.Add(obj);
                    }
                    output.Add(o);
                }

                return message;
            }
            catch (Exception e)
            {
                return e.ToString();
            }

        }
        */


        public void WriteStruct(string structName, string[] types, object[][] values)
        {
            int dataStreamLength = 0;

            for (int i = 0; i < values.Length; i++)
                dataStreamLength += StreamLength(types[i]) * values[i].Length;

            using (AdsStream dataStream = new AdsStream(dataStreamLength))
            using (AdsBinaryWriter writer = new AdsBinaryWriter(dataStream))
            {
                int varHandle = TcClient.CreateVariableHandle(structName);
                dataStream.Position = 0;

                for (int i = 0; i < values.Length; i++)
                {
                    foreach (object obj in values[i])
                    {
                        WriteObjectToWriter(writer, types[i], obj);
                        if (types[i] == "string") dataStream.Position += 81 - obj.ToString().Length;
                    }
                }

                TcClient.Write(varHandle, dataStream);
                writer.Flush();
            }
        }


        /// <summary>
        /// Internal util function that maps arbitrary object read actions to the corresponding AdsBinaryReader read function
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="typeName"></param>
        /// <returns></returns>
        private object ReadObjectFromReader(AdsBinaryReader reader, string typeName)
        {
            object value = "";
            switch (typeName)
            {
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "byte":
                    value = reader.ReadByte();
                    break;
                case "sint":
                    value = reader.ReadInt16();
                    break;
                case "usint":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                    value = reader.ReadInt32();
                    break;
                case "uint":
                    value = reader.ReadUInt32();
                    break;
                case "dint":
                    value = reader.ReadInt64();
                    break;
                case "udint":
                    value = reader.ReadUInt64();
                    break;
                case "real":
                    value = reader.ReadSingle();
                    break;
                case "lreal":
                    value = reader.ReadDouble();
                    break;
                case "time":
                    value = reader.ReadPlcTIME();
                    break;
                case "date":
                    value = reader.ReadPlcDATE();
                    break;
                default:
                    if (typeName.StartsWith("string"))
                    {
                        int length = typeName == "string" ? 80 : int.Parse(typeName.Substring(6));
                        value = reader.ReadPlcAnsiString(length);
                    }

                    else
                    {
                        Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Error,
                            string.Format("Can not read from AdsBinaryReader. Data type '{0}' not supported.", typeName));
                    }

                    break;
            }
            return value;
        }

        /// <summary>
        /// Internal util function that maps arbitrary object write actions to the corresponding AdsBinaryWriter write function
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="value"></param>
        private void WriteObjectToWriter(AdsBinaryWriter writer, string typeName, object value)
        {
            switch (typeName)
            {
                case "bool":
                    writer.Write(bool.Parse(value.ToString()));
                    break;
                case "byte":
                    writer.Write((byte)value);
                    break;
                case "sint":
                    writer.Write(short.Parse(value.ToString()));
                    break;
                case "int":
                    writer.Write(int.Parse(value.ToString()));
                    break;
                case "dint":
                    writer.Write(long.Parse(value.ToString()));
                    break;
                case "usint":
                    writer.Write(ushort.Parse(value.ToString()));
                    break;
                case "uint":
                    writer.Write(uint.Parse(value.ToString()));
                    break;
                case "udint":
                    writer.Write(ulong.Parse(value.ToString()));
                    break;
                case "real":
                    writer.Write(float.Parse(value.ToString(), CultureInfo.InvariantCulture.NumberFormat));
                    break;
                case "lreal":
                    writer.Write(double.Parse(value.ToString(), CultureInfo.InvariantCulture.NumberFormat));
                    break;
                case "time":
                    writer.WritePlcType(TimeSpan.Parse(value.ToString()));
                    break;
                case "date":
                    writer.WritePlcType(DateTime.Parse(value.ToString()));
                    break;
                default:
                    if (typeName.StartsWith("string"))
                    {

                        int length = typeName == "string" ? 80 : int.Parse(typeName.Substring(6));
                        writer.WritePlcAnsiString(value.ToString(), length);
                    }
                    else
                    {
                        Send_TcClient_EventHandling(DateTime.Now, LogTextCategory.Error,
                           string.Format("Can not write to AdsBinaryReader. Data type '{0}' not supported", typeName));
                    }

                    break;
            }
        }

        /// <summary>
        /// Internal util function that determins lengths of stream reads based on type name. Not very elegant. Can probably be simplified. Strings are a special case
        /// </summary>
        /// <param name="value"></param>
        /// <returns>Stream length in byte</returns>
        private static int StreamLength(string typeName)
        {
            ///CONTINUE HERE
            int streamLength = 0;
            if (typeName == "bool") streamLength = 1;
            else if (typeName == "byte") streamLength = 1;
            else if (typeName == "sint" || typeName == "usint") streamLength = 2;
            else if (typeName == "int" || typeName == "uint") streamLength = 4;
            else if (typeName == "dint" || typeName == "udint") streamLength = 8;
            else if (typeName == "real") streamLength = 4;
            else if (typeName == "lreal") streamLength = 8;
            else if (typeName == "string") streamLength = 81;
            else if (typeName.StartsWith("string")) streamLength = int.Parse(typeName.Substring(6));
            else if (typeName == "time") streamLength = 4;
            else if (typeName == "date") streamLength = 4;
            else return -1;

            return streamLength;
        }
    }
}