using System;
using System.Net;
using System.Xml;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Web.Services.Protocols;
using System.Text.RegularExpressions;

namespace MSNPSharp
{
    using MSNPSharp.Core;
    using MSNPSharp.MSNWS.MSNRSIService;
    using MSNPSharp.MSNWS.MSNOIMStoreService;

    /// <summary>
    /// This delegate is used when an OIM was received.
    /// </summary>
    /// <param name="sender">The sender's email</param>
    /// <param name="e">OIMReceivedEventArgs</param>
    public delegate void OIMReceivedEventHandler(object sender, OIMReceivedEventArgs e);

    [Serializable()]
    public class OIMReceivedEventArgs : EventArgs
    {
        private bool isRead = true;
        private Guid guid = Guid.Empty;
        private DateTime receivedTime = DateTime.Now;
        private string email;
        private string message;

        public DateTime ReceivedTime
        {
            get
            {
                return receivedTime;
            }
        }

        /// <summary>
        /// Sender account.
        /// </summary>
        public string Email
        {
            get
            {
                return email;
            }
        }

        /// <summary>
        /// Text message.
        /// </summary>
        public string Message
        {
            get
            {
                return message;
            }
        }

        /// <summary>
        /// Message ID
        /// </summary>
        public Guid Guid
        {
            get
            {
                return guid;
            }
        }

        /// <summary>
        /// Set this to true if you don't want to receive this message
        /// next time you login.
        /// </summary>
        public bool IsRead
        {
            get
            {
                return isRead;
            }
            set
            {
                isRead = value;
            }
        }

        public OIMReceivedEventArgs(DateTime rcvTime, Guid g, string account, string msg)
        {
            receivedTime = rcvTime;
            guid = g;
            email = account;
            message = msg;
        }
    }

    /// <summary>
    /// Provides webservice operation for offline messages
    /// </summary>
    public class OIMService : MSNService
    {
        /// <summary>
        /// Occurs when receive an OIM.
        /// </summary>
        public event OIMReceivedEventHandler OIMReceived;

        public OIMService(NSMessageHandler nsHandler)
            : base(nsHandler)
        {
        }

        private RSIService CreateRSIService()
        {
            NSMessageHandler.MSNTicket.RenewIfExpired(SSOTicketType.Web);
            string[] TandP = NSMessageHandler.MSNTicket.SSOTickets[SSOTicketType.Web].Ticket.Split(new string[] { "t=", "&p=" }, StringSplitOptions.None);

            RSIService rsiService = new RSIService();
            rsiService.Proxy = WebProxy;
            rsiService.Timeout = Int32.MaxValue;
            rsiService.PassportCookieValue = new PassportCookie();
            rsiService.PassportCookieValue.t = TandP[1];
            rsiService.PassportCookieValue.p = TandP[2];
            return rsiService;
        }

        private OIMStoreService CreateOIMStoreService()
        {
            NSMessageHandler.MSNTicket.RenewIfExpired(SSOTicketType.OIM);

            OIMStoreService oimService = new OIMStoreService();
            oimService.Proxy = WebProxy;
            oimService.TicketValue = new Ticket();
            oimService.TicketValue.passport = NSMessageHandler.MSNTicket.SSOTickets[SSOTicketType.OIM].Ticket;
            oimService.TicketValue.lockkey = NSMessageHandler.MSNTicket.OIMLockKey;
            oimService.TicketValue.appid = NSMessageHandler.Credentials.ClientID;
            return oimService;
        }

        internal void ProcessOIM(MSGMessage message, bool initial)
        {
            if (OIMReceived == null)
                return;

            string xmlstr = message.MimeHeader["Mail-Data"];
            if ("too-large" == xmlstr && NSMessageHandler.MSNTicket.SSOTickets.ContainsKey(SSOTicketType.Web))
            {
                RSIService rsiService = CreateRSIService();
                rsiService.GetMetadataCompleted += delegate(object sender, GetMetadataCompletedEventArgs e)
                {
                    if (!e.Cancelled && e.Error == null)
                    {
                        if (Settings.TraceSwitch.TraceVerbose)
                            Trace.WriteLine("GetMetadata completed.");

                        if (e.Result != null && e.Result is Array)
                        {
                            foreach (XmlNode m in (XmlNode[])e.Result)
                            {
                                if (m.Name == "MD")
                                {
                                    processOIMS(((XmlNode)m).ParentNode.InnerXml, initial);
                                    break;
                                }
                            }
                        }
                    }
                    else if (e.Error != null)
                    {
                        OnServiceOperationFailed(sender,
                            new ServiceOperationFailedEventArgs("ProcessOIM", e.Error));
                    }
                    ((IDisposable)sender).Dispose();
                    return;
                };
                rsiService.GetMetadataAsync(new GetMetadataRequestType(), new object());
                return;
            }
            processOIMS(xmlstr, initial);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="xmldata"></param>
        /// <param name="initial">if true, get all oims and sort by receive date</param>
        private void processOIMS(string xmldata, bool initial)
        {
            if (OIMReceived == null)
                return;

            if (!NSMessageHandler.MSNTicket.SSOTickets.ContainsKey(SSOTicketType.Web))
                return;

            XmlDocument xdoc = new XmlDocument();
            xdoc.LoadXml(xmldata);
            XmlNodeList xnodlst = xdoc.GetElementsByTagName("M");
            List<string> guidstodelete = new List<string>();
            List<OIMReceivedEventArgs> initialOIMS = new List<OIMReceivedEventArgs>();
            int oimdeletecount = xnodlst.Count;

            Regex regmsg = new Regex("\n\n[^\n]+");
            Regex regsenderdata = new Regex("From:(?<encode>=.*=)<(?<mail>.+)>\n");

            foreach (XmlNode m in xnodlst)
            {
                DateTime rt = DateTime.Now;
                Int32 size = 0;
                Guid guid = Guid.Empty;
                String email = String.Empty;

                String message = String.Empty;

                foreach (XmlNode a in m)
                {
                    switch (a.Name)
                    {
                        case "RT":
                            rt = XmlConvert.ToDateTime(a.InnerText, XmlDateTimeSerializationMode.RoundtripKind);
                            break;

                        case "SZ":
                            size = Convert.ToInt32(a.InnerText);
                            break;

                        case "E":
                            email = a.InnerText;
                            break;

                        case "I":
                            guid = new Guid(a.InnerText);
                            break;
                    }
                }

                RSIService rsiService = CreateRSIService();
                rsiService.GetMessageCompleted += delegate(object service, GetMessageCompletedEventArgs e)
                {
                    if (!e.Cancelled && e.Error == null)
                    {
                        Match mch = regsenderdata.Match(e.Result.GetMessageResult);

                        string strencoding = "utf-8";
                        if (mch.Groups["encode"].Value != String.Empty)
                        {
                            strencoding = mch.Groups["encode"].Value.Split('?')[1];
                        }
                        Encoding encode = Encoding.GetEncoding(strencoding);
                        message = encode.GetString(Convert.FromBase64String(regmsg.Match(e.Result.GetMessageResult).Value.Trim()));

                        OIMReceivedEventArgs orea = new OIMReceivedEventArgs(rt, guid, email, message);

                        if (initial)
                        {
                            initialOIMS.Add(orea);

                            // Is this the last OIM?
                            if (initialOIMS.Count == oimdeletecount)
                            {
                                initialOIMS.Sort(CompareDates);
                                foreach (OIMReceivedEventArgs ea in initialOIMS)
                                {
                                    OnOIMReceived(this, ea);
                                    if (ea.IsRead)
                                    {
                                        guidstodelete.Add(ea.Guid.ToString());
                                    }
                                    if (0 == --oimdeletecount && guidstodelete.Count > 0)
                                    {
                                        DeleteOIMMessages(guidstodelete.ToArray());
                                    }
                                }
                            }
                        }
                        else
                        {
                            OnOIMReceived(this, orea);
                            if (orea.IsRead)
                            {
                                guidstodelete.Add(guid.ToString());
                            }
                            if (0 == --oimdeletecount && guidstodelete.Count > 0)
                            {
                                DeleteOIMMessages(guidstodelete.ToArray());
                            }
                        }

                    }
                    else if (e.Error != null)
                    {
                        OnServiceOperationFailed(rsiService, new ServiceOperationFailedEventArgs("ProcessOIM", e.Error));
                    }
                    return;
                };

                GetMessageRequestType request = new GetMessageRequestType();
                request.messageId = guid.ToString();
                request.alsoMarkAsRead = false;
                rsiService.GetMessageAsync(request, new object());
            }
        }

        private static int CompareDates(OIMReceivedEventArgs x, OIMReceivedEventArgs y)
        {
            return x.ReceivedTime.CompareTo(y.ReceivedTime);
        }

        private void DeleteOIMMessages(string[] guids)
        {
            if (!NSMessageHandler.MSNTicket.SSOTickets.ContainsKey(SSOTicketType.Web))
                return;

            RSIService rsiService = CreateRSIService();
            rsiService.DeleteMessagesCompleted += delegate(object service, DeleteMessagesCompletedEventArgs e)
            {
                if (!e.Cancelled && e.Error == null)
                {
                    if (Settings.TraceSwitch.TraceVerbose)
                        Trace.WriteLine("DeleteMessages completed.");
                }
                else if (e.Error != null)
                {
                    OnServiceOperationFailed(rsiService,
                            new ServiceOperationFailedEventArgs("ProcessOIM", e.Error));
                }
                ((IDisposable)service).Dispose();
                return;
            };

            DeleteMessagesRequestType request = new DeleteMessagesRequestType();
            request.messageIds = guids;
            rsiService.DeleteMessagesAsync(request, new object());
        }

        private string _RunGuid = Guid.NewGuid().ToString();

        /// <summary>
        /// Send an offline message to a contact.
        /// </summary>
        /// <param name="account">Target user</param>
        /// <param name="msg">Plain text message</param>
        public void SendOIMMessage(string account, string msg)
        {
            Contact contact = NSMessageHandler.ContactList[account]; // Only PassportMembers can receive oims.
            if (NSMessageHandler.MSNTicket.SSOTickets.ContainsKey(SSOTicketType.OIM) && contact != null && contact.ClientType == ClientType.PassportMember && contact.OnAllowedList)
            {
                StringBuilder messageTemplate = new StringBuilder(
                    "MIME-Version: 1.0\r\n"
                  + "Content-Type: text/plain; charset=UTF-8\r\n"
                  + "Content-Transfer-Encoding: base64\r\n"
                  + "X-OIM-Message-Type: OfflineMessage\r\n"
                  + "X-OIM-Run-Id: {{run_id}}\r\n"
                  + "X-OIM-Sequence-Num: {seq-num}\r\n"
                  + "\r\n"
                  + "{base64_msg}\r\n"
                );

                messageTemplate.Replace("{base64_msg}", Convert.ToBase64String(Encoding.UTF8.GetBytes(msg), Base64FormattingOptions.InsertLineBreaks));
                messageTemplate.Replace("{seq-num}", contact.OIMCount.ToString());
                messageTemplate.Replace("{run_id}", _RunGuid);

                string message = messageTemplate.ToString();

                OIMUserState userstate = new OIMUserState(contact.OIMCount, account);

                string name48 = NSMessageHandler.Owner.Name;
                if (name48.Length > 48)
                    name48 = name48.Substring(47);

                OIMStoreService oimService = CreateOIMStoreService();
                oimService.FromValue = new From();
                oimService.FromValue.memberName = NSMessageHandler.Owner.Mail;
                oimService.FromValue.friendlyName = "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name48)) + "?=";
                oimService.FromValue.buildVer = "8.5.1302";
                oimService.FromValue.msnpVer = "MSNP15";
                oimService.FromValue.lang = System.Globalization.CultureInfo.CurrentCulture.Name;
                oimService.FromValue.proxy = "MSNMSGR";

                oimService.ToValue = new To();
                oimService.ToValue.memberName = account;

                oimService.Sequence = new SequenceType();
                oimService.Sequence.Identifier = new AttributedURI();
                oimService.Sequence.Identifier.Value = "http://messenger.msn.com";
                oimService.Sequence.MessageNumber = userstate.oimcount;

                oimService.StoreCompleted += delegate(object service, StoreCompletedEventArgs e)
                {
                    oimService = service as OIMStoreService;
                    if (e.Cancelled == false && e.Error == null)
                    {
                        SequenceAcknowledgmentAcknowledgmentRange range = oimService.SequenceAcknowledgmentValue.AcknowledgmentRange[0];
                        if (range.Lower == userstate.oimcount && range.Upper == userstate.oimcount)
                        {
                            contact.OIMCount++; // Sent successfully.
                            if (Settings.TraceSwitch.TraceVerbose)
                                Trace.WriteLine("An OIM Message has been sent: " + userstate.account + ", runId = " + _RunGuid);
                        }
                    }
                    else if (e.Error != null && e.Error is SoapException)
                    {
                        SoapException soapexp = e.Error as SoapException;
                        if (soapexp.Code.Name == "AuthenticationFailed")
                        {
                            NSMessageHandler.MSNTicket.OIMLockKey = QRYFactory.CreateQRY(NSMessageHandler.Credentials.ClientID, NSMessageHandler.Credentials.ClientCode, soapexp.Detail.InnerText);
                            oimService.TicketValue.lockkey = NSMessageHandler.MSNTicket.OIMLockKey;
                        }
                        else if (soapexp.Code.Name == "SenderThrottleLimitExceeded")
                        {
                            if (Settings.TraceSwitch.TraceVerbose)
                                Trace.WriteLine("OIM: SenderThrottleLimitExceeded. Waiting 11 seconds...");

                            System.Threading.Thread.Sleep(11111); // wait 11 seconds.
                        }
                        if (userstate.RecursiveCall++ < 5)
                        {
                            oimService.StoreAsync(MessageType.text, message, userstate); // Call this delegate again.
                            return;
                        }
                        OnServiceOperationFailed(oimService,
                            new ServiceOperationFailedEventArgs("SendOIMMessage", e.Error));
                    }
                };
                oimService.StoreAsync(MessageType.text, message, userstate);
            }
        }

        protected virtual void OnOIMReceived(object sender, OIMReceivedEventArgs e)
        {
            if (OIMReceived != null)
            {
                OIMReceived(sender, e);
            }
        }
    }

    internal class OIMUserState
    {
        public int RecursiveCall = 0;
        public readonly ulong oimcount;
        public readonly string account = String.Empty;
        public OIMUserState(ulong oimCount, string account)
        {
            this.oimcount = oimCount;
            this.account = account;
        }
    }
};
