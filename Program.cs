using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using HidSharp;

class Program
{
    const int VID = 0x0416;
    const int PID = 0x5020;

    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法: BadgeApp.exe \"TEXT\" [-speed N] [-animationtype N] [-blink true/false] [-framelight true/false] [-continuous true/false]");
            return;
        }

        string text = args[0]; // 保留原始大小写
        byte speed = 6;
        byte animationtype = 0;
        bool blink = false;
        bool framelight = false;
        bool continuous = false;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i].ToLower();
            if (arg == "-speed" && i + 1 < args.Length && byte.TryParse(args[i + 1], out byte s)) { speed = s; i++; }
            else if (arg == "-animationtype" && i + 1 < args.Length && byte.TryParse(args[i + 1], out byte a)) { animationtype = a; i++; }
            else if (arg == "-blink" && i + 1 < args.Length && bool.TryParse(args[i + 1], out bool b)) { blink = b; i++; }
            else if (arg == "-framelight" && i + 1 < args.Length && bool.TryParse(args[i + 1], out bool f)) { framelight = f; i++; }
            else if (arg == "-continuous" && i + 1 < args.Length && bool.TryParse(args[i + 1], out bool c)) { continuous = c; i++; }
        }

        byte charactercount = (byte)text.Length;

        var dev = DeviceList.Local.GetHidDevices(VID, PID).FirstOrDefault();
        if (dev == null) { Console.WriteLine("设备未找到！"); return; }

        Console.WriteLine($"找到设备: {dev}");
        using (var stream = dev.Open())
        {
            int outLen = dev.GetMaxOutputReportLength();
            Console.WriteLine($"OutputReportLength = {outLen}");

            // 发送控制包
            SendReport(stream, GenerateControlPacket(speed, animationtype, charactercount, blink, framelight), outLen);
            Thread.Sleep(20);

            // 生成点阵数据分包
            List<byte[]> dataPackets = continuous
                ? GenerateContinuousDataPackets(text)
                : GenerateCharByCharDataPackets(text);

            // 发送点阵包
            foreach (var packet in dataPackets)
            {
                SendReport(stream, packet, outLen);
                Thread.Sleep(10);
            }
        }

        Console.WriteLine("发送完成！");
    }

    static void SendReport(HidStream stream, byte[] payload, int outLen)
    {
        byte[] report = new byte[outLen];
        report[0] = 0;
        int len = Math.Min(payload.Length, outLen - 1);
        Array.Copy(payload, 0, report, 1, len);
        stream.Write(report);
    }

    static byte[] GenerateControlPacket(byte speed, byte animationtype, byte charactercount, bool blink, bool framelight)
    {
        byte[] packet = new byte[64]
        {
            0x77,0x61,0x6E,0x67,0x00,0x00,0x00,0x00,
            0x00,0x00,0x30,0x30,0x30,0x30,0x30,0x30,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x14,0x0C,
            0x09,0x17,0x25,0x33,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        };
        if (blink) packet[6] = 1;
        if (framelight) packet[7] = 1;
        packet[8] = (byte)(16 * (speed - 1) + animationtype);
        packet[17] = charactercount;
        return packet;
    }

    static List<byte[]> GenerateCharByCharDataPackets(string text)
    {
        int charWidth = 8;
        int charHeight = 11;
        int maxDataPerPacket = 64;
        List<byte[]> packets = new List<byte[]>();
        List<byte> currentPacket = new List<byte>();

        using (Bitmap bmp = new Bitmap(charWidth, charHeight))
        using (Graphics g = Graphics.FromImage(bmp))
        using (Font font = new Font("SimSun", 11, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            foreach (char c in text)
            {
                g.Clear(Color.Black);
                g.DrawString(c.ToString(), font, Brushes.White, 0, 0);

                byte[] charBytes = new byte[charHeight];
                for (int y = 0; y < charHeight; y++)
                {
                    byte row = 0;
                    for (int x = 0; x < charWidth; x++)
                    {
                        Color pixel = bmp.GetPixel(x, y);
                        if (pixel.GetBrightness() > 0.5f) row |= (byte)(1 << (7 - x));
                    }
                    charBytes[y] = row;
                }

                int offset = 0;
                while (offset < charBytes.Length)
                {
                    int spaceLeft = maxDataPerPacket - currentPacket.Count;
                    int copyLen = Math.Min(spaceLeft, charBytes.Length - offset);
                    currentPacket.AddRange(charBytes.Skip(offset).Take(copyLen));
                    offset += copyLen;

                    if (currentPacket.Count == maxDataPerPacket)
                    {
                        packets.Add(currentPacket.ToArray());
                        currentPacket.Clear();
                    }
                }
            }
        }

        if (currentPacket.Count > 0)
            packets.Add(currentPacket.ToArray());

        return packets;
    }

    static List<byte[]> GenerateContinuousDataPackets(string text)
    {
        int charWidth = 8;
        int charHeight = 11;
        int maxDataPerPacket = 64;
        List<byte[]> packets = new List<byte[]>();
        List<byte> currentPacket = new List<byte>();

        int imgWidth = charWidth * text.Length;

        using (Bitmap bmp = new Bitmap(imgWidth, charHeight))
        using (Graphics g = Graphics.FromImage(bmp))
        using (Font font = new Font("SimSun", 11, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(Color.Black);
            g.DrawString(text, font, Brushes.White, 0, 0);

            for (int xChar = 0; xChar < text.Length; xChar++)
            {
                for (int y = 0; y < charHeight; y++)
                {
                    byte row = 0;
                    for (int x = 0; x < charWidth; x++)
                    {
                        Color pixel = bmp.GetPixel(xChar * charWidth + x, y);
                        if (pixel.GetBrightness() > 0.5f) row |= (byte)(1 << (7 - x));
                    }
                    currentPacket.Add(row);
                    if (currentPacket.Count == maxDataPerPacket)
                    {
                        packets.Add(currentPacket.ToArray());
                        currentPacket.Clear();
                    }
                }
            }
        }

        if (currentPacket.Count > 0)
            packets.Add(currentPacket.ToArray());

        return packets;
    }
}
