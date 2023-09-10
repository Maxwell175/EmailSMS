using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Stubble.Core.Builders;

var stubble = new StubbleBuilder().Build();
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var port = new SerialPort(config["ModemPort"]!);

var pendingMessages = new List<int>();

#region BufferLayer

// Problem is, its possible to get a notification about a new message in the middle of another process.
// If we were to use naive port.ReadLines everywhere this trips up because we end up catching a new message
// notification rather than the command response that we expected.
// To work around this, we have a quick and dirty extra buffer layer that constantly watches for new message
// notifications and pushes them to the pendingMessages list for later processing. This seems to successfully
// survive a burst of at least 5 messages at once.
var buf = "";

void CleanPortBuf()
{
    while (buf.Contains("\r\n+CMTI: "))
    {
        var newMessageIdx = buf.IndexOf("\r\n+CMTI: ", StringComparison.Ordinal);
        var endOfNewMessage = buf.IndexOf("\r\n", newMessageIdx + "\r\n+CMTI: ".Length, StringComparison.Ordinal) + 2;
        var newMessageText = buf[newMessageIdx..endOfNewMessage].Trim();
        var smsIdx = int.Parse(newMessageText.Split(',').Last()); // Get the new SMS index.
        pendingMessages.Add(smsIdx);
        buf = buf[..newMessageIdx] + buf[endOfNewMessage..];
    }
}

string PortReadLine()
{
    buf += port.ReadExisting();
    if (!buf.Contains('\n'))
    {
        buf += port.ReadLine() + '\n';
        buf += port.ReadExisting();
    }

    CleanPortBuf();

    var lineIdx = buf.IndexOf('\n');
    var result = buf[..lineIdx];
    buf = buf[(lineIdx + 1)..];
    return result;
}

string PortReadExisting()
{
    buf += port.ReadExisting();

    CleanPortBuf();

    var result = buf;
    buf = "";
    return result;
}

#endregion

string AtCmd(string command)
{
    port.Write(command + "\r");
    PortReadLine(); // Skip line because it echos back.
    return PortReadLine().Trim('\r');
}

port.Open();

// Send a blank AT command to make sure we have the right serial port.
if (AtCmd("AT") != "OK")
    throw new Exception("Failed to verify modem interface.");

var imapClient = new ImapClient();
imapClient.Connect(config["ImapServer"], int.Parse(config["ImapPort"]!));
imapClient.Authenticate(config["MailUsername"], config["MailPassword"]);

imapClient.Inbox.Open(FolderAccess.ReadOnly);
var inboxCount = imapClient.Inbox.Count;
var lastInboxCheck = DateTime.Now;
imapClient.Inbox.Close();

while (port.IsOpen)
{
    // Loop while there are no new messages.
    while (port.BytesToRead == 0 && pendingMessages.Count == 0)
    {
        Thread.Sleep(100);
        if ((DateTime.Now - lastInboxCheck).Seconds > 1)
        {
            imapClient.Inbox.Open(FolderAccess.ReadOnly);
            var newInboxCount = imapClient.Inbox.Count;
            var newMessages = newInboxCount - inboxCount;
            if (newMessages > 0)
            {
                Console.WriteLine($"Got {newMessages} new message(s).");
                for (var i = inboxCount; i < newInboxCount; i++)
                {
                    var newMessage = imapClient.Inbox.GetMessage(i);

                    var allowedEmails = config["AllowedSmsSenders"].Split(';');
                    if (!newMessage.From.OfType<MailboxAddress>().All(a => allowedEmails.Contains(a.Address)))
                    {
                        // Ignore emails from addresses that are not allowed.
                        continue;
                    }
                    
                    var destNumber = Regex.Replace(newMessage.Subject, @"\p{C}+", string.Empty);
                    if (Regex.IsMatch(destNumber, "\\+?[0-9]+"))
                    {
                        AtCmd("AT+CMGF=1");
                        port.Write($"AT+CMGS=\"{destNumber}\"\r");
                        port.Write(newMessage.BodyParts.OfType<TextPart>().First().Text);
                        port.Write("\x1a");
                        while (!PortReadLine().StartsWith("+CMGS: "))
                        {
                        }

                        while (!PortReadLine().StartsWith("OK"))
                        {
                        }

                        PortReadExisting();

                        Console.WriteLine("Successfully sent message.");
                    }
                }
            }

            inboxCount = newInboxCount;
            lastInboxCheck = DateTime.Now;

            imapClient.Inbox.Close();
        }
    }

    PortReadExisting();

    foreach (var smsIdx in pendingMessages.ToList())
    {
        var messageInfo = AtCmd($"AT+CMGR={smsIdx}").Split(',').Select(p => p.Trim('"')).ToList();
        // ex: "+CMGR: \"REC UNREAD\",\"+12223334444\",\"\",\"23/09/23,21:10:59-28\"\r"
        var body = PortReadExisting()[..^6]; // Remove the last 6 characters which are always "\r\nOK\r\n"
        var fromNumber = messageInfo[1];
        var recievedDt = new DateTimeOffset( // Parse the recieved date.
                DateTime.ParseExact(messageInfo[3] + messageInfo[4][..8], "yy/MM/ddHH:mm:ss",
                    CultureInfo.InvariantCulture),
                TimeSpan.FromMinutes(int.Parse(messageInfo[4][8..]) * 15))
            .ToLocalTime(); // Timezone is in quarter hours. Convert to local timezone.

        var messageData = new
        {
            FromNumber = fromNumber,
            ReceivedTime = recievedDt,
            Body = body
        };

        // Erase the message from the SIM module once we have processed it.
        AtCmd($"AT+CMGD={smsIdx}");

        // Send the email.
        using (var smtpClient = new SmtpClient())
        {
            smtpClient.Connect(config["SmtpServer"], int.Parse(config["SmtpPort"]!), SecureSocketOptions.StartTls);
            smtpClient.Authenticate(config["MailUsername"], config["MailPassword"]);

            // Build up the message.
            var message = new MimeMessage();
            message.From.Add(InternetAddress.Parse(config["MailFrom"]));
            message.To.AddRange(config["SmsRecipients"]!.Split(';').Select(InternetAddress.Parse));
            // We are using {{mustache}} syntax to allow simple text customization.
            message.Subject = stubble.Render(config["Text:ReceivedEmailSubject"]!, messageData);
            message.Body = new TextPart("plain")
            {
                Text = stubble.Render(config["Text:ReceivedEmailBody"]!, messageData)
            };

            smtpClient.Send(message);
            smtpClient.Disconnect(true);
        }

        Console.WriteLine("Successfully received message.");
        pendingMessages.Remove(smsIdx);
    }
}

port.Close();