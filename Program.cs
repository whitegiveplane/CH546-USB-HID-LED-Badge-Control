using System.Drawing;
using System.Drawing.Text;
using HidSharp;

class Program
{
    const int VID = 0x0416;
    const int PID = 0x5020;

    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: USB-LED-Badge-Control \"TEXT\" [-speed N(1-8)] [-animationtype N(0-11)] [-blink] [-scrollinglight] [-separate]");
            return;
        }

        string text = args[0];
        byte speed = 6;
        byte animationtype = 0;
        bool blink = false;
        bool scrollinglight = false;
        bool continuous = true; // default: continuous (streaming) mode. Use -separate to switch to per-character mode.

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i].ToLower();

            if (arg == "-speed" && i + 1 < args.Length && byte.TryParse(args[i + 1], out byte s))
            {
                speed = s;
                i++;
            }
            else if (arg == "-animationtype" && i + 1 < args.Length && byte.TryParse(args[i + 1], out byte a))
            {
                animationtype = a;
                i++;
            }
            else if (arg == "-blink")
            {
                blink = true;
            }
            else if (arg == "-scrollinglight")
            {
                scrollinglight = true;
            }
            else if (arg == "-separate")
            {
                // when -separate is provided, use per-character (separate) mode instead of continuous streaming
                continuous = false;
            }
        }
        text = TextExtender(text);
        short charactercount = (short)text.Length;
        Console.WriteLine("String length: " + charactercount);

        var dev = DeviceList.Local.GetHidDevices(VID, PID).FirstOrDefault();
        if (dev == null)
        {
            Console.WriteLine("Device NOT found! Check connection.");
            return;
        }

        Console.WriteLine($"Device found: {dev}");

        using (var stream = dev.Open())
        {
            int outLen = dev.GetMaxOutputReportLength();
            Console.WriteLine($"OutputReportLength = {outLen}");

            List<byte[]> dataPackets = continuous
                ? GenerateContinuousDataPackets(text)
                : GenerateCharByCharDataPackets(text);

            float packetCount = dataPackets.Count;
            float indicator = 0;
            float progress = 0;

            Console.WriteLine("Total Packet Count: " + packetCount);
            Console.WriteLine("Total Characters Sent (est.): " + Math.Ceiling(packetCount * 64 / 11));

            // Send control packet
            SendReport(stream, GenerateControlPacket(speed, animationtype, charactercount, blink, scrollinglight), outLen);

            // Send pixel packets
            foreach (var packet in dataPackets)
            {
                SendReport(stream, packet, outLen);

                indicator++;
                progress = indicator / packetCount;

                Console.WriteLine("Progress: " + (int)(progress * 100) + "%");
                Console.CursorTop -= 1;
            }
        }

        Console.WriteLine("All packets sent.");
    }

    static void SendReport(HidStream stream, byte[] payload, int outLen)
    {
        byte[] report = new byte[outLen];
        report[0] = 0;

        int len = Math.Min(payload.Length, outLen - 1);
        Array.Copy(payload, 0, report, 1, len);

        stream.Write(report);
    }

    static byte[] GenerateControlPacket(byte speed, byte animationtype, short charactercount, bool blink, bool scrollinglight)
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
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
        };

        // Blink flag
        if (blink) packet[6] = 1;

        // Scrolling-light flag
        if (scrollinglight) packet[7] = 1;

        // Speed and animation mode
        packet[8] = (byte)(16 * (speed - 1) + animationtype);

        // Character count
        if (charactercount <= 255)
        {
            packet[17] = (byte)charactercount;
        }
        else
        {
            packet[16] = (byte)(charactercount / 256);
            packet[17] = (byte)(charactercount % 256);
        }

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
        using (Font font = new Font("Consolas", 11, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            foreach (char c in text)
            {
                g.Clear(Color.Black);
                g.DrawString(c.ToString(), font, Brushes.White, 0, -1);

                byte[] charBytes = new byte[charHeight];

                for (int y = 0; y < charHeight; y++)
                {
                    byte row = 0;

                    for (int x = 0; x < charWidth; x++)
                    {
                        Color pixel = bmp.GetPixel(x, y);
                        if (pixel.GetBrightness() > 0.7f)
                            row |= (byte)(1 << (7 - x));
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

        int imgWidth = 0;
        int halfwidthcharcount = 0;
        int fullwidthcharcount = 0;

        foreach (char c in text)
        {
            if (c < 128)
            {
                imgWidth += 8;
                halfwidthcharcount++;
            }
            else
            {
                imgWidth += 10;
                fullwidthcharcount++;
            }
        }

        if (imgWidth % charWidth != 0)
            imgWidth += charWidth - imgWidth % charWidth;

        Console.WriteLine("Half-width chars: " + halfwidthcharcount);
        Console.WriteLine("Full-width chars: " + fullwidthcharcount);

        using (Bitmap bmp = new Bitmap(imgWidth, charHeight))
        using (Graphics g = Graphics.FromImage(bmp))
        using (Font font = new Font("Consolas", (float)10.5, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(Color.Black);

            var fmt = new StringFormat(StringFormatFlags.MeasureTrailingSpaces);
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            fmt.FormatFlags |= StringFormatFlags.NoClip;

            g.DrawString(text, font, Brushes.White, 0, -1, fmt);

            for (int xChar = 0; xChar < imgWidth / charWidth; xChar++)
            {
                for (int y = 0; y < charHeight; y++)
                {
                    byte row = 0;

                    for (int x = 0; x < charWidth; x++)
                    {
                        Color pixel = bmp.GetPixel(xChar * charWidth + x, y);
                        if (pixel.GetBrightness() > 0.7f)
                            row |= (byte)(1 << (7 - x));
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


    static String TextExtender(String text)
    {
        int n = 0;
        byte p = 0;
        foreach (char c in text)
        {
            if (c >= 128)
            {
                p++;
            }
            if (p > 1)
            {
                p = 0;
                continue;
            }
            n++;

        }
        text += new string(' ', n);
        return text;
    }
}
