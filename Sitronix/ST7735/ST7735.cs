using System;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Spi;

namespace GHIElectronics.TinyCLR.Drivers.Sitronix.ST7735 {
    public enum ST7735CommandId : byte {
        //System
        NOP = 0x00,
        SWRESET = 0x01,
        RDDID = 0x04,
        RDDST = 0x09,
        RDDPM = 0x0A,
        RDDMADCTL = 0x0B,
        RDDCOLMOD = 0x0C,
        RDDIM = 0x0D,
        RDDSM = 0x0E,
        SLPIN = 0x10,
        SLPOUT = 0x11,
        PTLON = 0x12,
        NORON = 0x13,
        INVOFF = 0x20,
        INVON = 0x21,
        GAMSET = 0x26,
        DISPOFF = 0x28,
        DISPON = 0x29,
        CASET = 0x2A,
        RASET = 0x2B,
        RAMWR = 0x2C,
        RAMRD = 0x2E,
        PTLAR = 0x30,
        TEOFF = 0x34,
        TEON = 0x35,
        MADCTL = 0x36,
        IDMOFF = 0x38,
        IDMON = 0x39,
        COLMOD = 0x3A,
        RDID1 = 0xDA,
        RDID2 = 0xDB,
        RDID3 = 0xDC,

        //Panel
        FRMCTR1 = 0xB1,
        FRMCTR2 = 0xB2,
        FRMCTR3 = 0xB3,
        INVCTR = 0xB4,
        DISSET5 = 0xB6,
        PWCTR1 = 0xC0,
        PWCTR2 = 0xC1,
        PWCTR3 = 0xC2,
        PWCTR4 = 0xC3,
        PWCTR5 = 0xC4,
        VMCTR1 = 0xC5,
        VMOFCTR = 0xC7,
        WRID2 = 0xD1,
        WRID3 = 0xD2,
        NVCTR1 = 0xD9,
        NVCTR2 = 0xDE,
        NVCTR3 = 0xDF,
        GAMCTRP1 = 0xE0,
        GAMCTRN1 = 0xE1,
    }

    public enum DataFormat {
        Rgb565 = 0,
        Rgb444 = 1
    }

    public enum ScreenSize {
        _160x128,
        _132x130,
        _128x128,
    }

    public class ST7735Controller {
        private readonly byte[] buffer1 = new byte[1];
        private readonly byte[] buffer4 = new byte[4];
        private readonly SpiDevice spi;
        private readonly GpioPin control;
        private readonly GpioPin reset;
        private readonly ScreenSize screenSize;

        private int bpp;
        private bool rowColumnSwapped;

        public DataFormat DataFormat { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public int MaxWidth => this.rowColumnSwapped ? (this.screenSize == ScreenSize._160x128 ? 160 : this.screenSize == ScreenSize._132x130 ? 132 : 128) : (this.screenSize == ScreenSize._160x128 ? 128 : this.screenSize == ScreenSize._132x130 ? 130 : 128);
        public int MaxHeight => this.rowColumnSwapped ? (this.screenSize == ScreenSize._160x128 ? 128 : this.screenSize == ScreenSize._132x130 ? 130 : 128) : (this.screenSize == ScreenSize._160x128 ? 160 : this.screenSize == ScreenSize._132x130 ? 132 : 128);

        public static SpiConnectionSettings GetConnectionSettings(SpiChipSelectType chipSelectType, GpioPin chipSelectLine) => new SpiConnectionSettings {
            Mode = SpiMode.Mode3,
            ClockFrequency = 12_000_000,
            ChipSelectType = chipSelectType,
            ChipSelectLine = chipSelectLine
        };

        public ST7735Controller(SpiDevice spi, GpioPin control) : this(spi, control, null) {

        }

        public ST7735Controller(SpiDevice spi, GpioPin control, GpioPin reset) : this(spi, control, reset, ScreenSize._160x128) {

        }
        public ST7735Controller(SpiDevice spi, GpioPin control, GpioPin reset, ScreenSize screenSize) {
            this.spi = spi;

            this.control = control;
            this.control.SetDriveMode(GpioPinDriveMode.Output);

            this.reset = reset;
            this.reset?.SetDriveMode(GpioPinDriveMode.Output);

            this.screenSize = screenSize;

            this.Reset();
            this.Initialize();
            this.SetDataFormat(DataFormat.Rgb565);
            this.SetDataAccessControl(false, false, false, false);
            this.SetDrawWindow(0, 0, this.MaxWidth, this.MaxHeight);
        }

        private void Reset() {
            if (this.reset == null)
                return;

            this.reset.Write(GpioPinValue.Low);
            Thread.Sleep(50);

            this.reset.Write(GpioPinValue.High);
            Thread.Sleep(200);
        }

        private void Initialize() {
            this.SendCommand(ST7735CommandId.SWRESET);
            Thread.Sleep(120);

            this.SendCommand(ST7735CommandId.SLPOUT);
            Thread.Sleep(120);

            this.SendCommand(ST7735CommandId.FRMCTR1);
            this.SendData(0x01);
            this.SendData(0x2C);
            this.SendData(0x2D);

            this.SendCommand(ST7735CommandId.FRMCTR2);
            this.SendData(0x01);
            this.SendData(0x2C);
            this.SendData(0x2D);

            this.SendCommand(ST7735CommandId.FRMCTR3);
            this.SendData(0x01);
            this.SendData(0x2C);
            this.SendData(0x2D);
            this.SendData(0x01);
            this.SendData(0x2C);
            this.SendData(0x2D);

            this.SendCommand(ST7735CommandId.INVCTR);
            this.SendData(0x07);

            this.SendCommand(ST7735CommandId.PWCTR1);
            this.SendData(0xA2);
            this.SendData(0x02);
            this.SendData(0x84);

            this.SendCommand(ST7735CommandId.PWCTR2);
            this.SendData(0xC5);

            this.SendCommand(ST7735CommandId.PWCTR3);
            this.SendData(0x0A);
            this.SendData(0x00);

            this.SendCommand(ST7735CommandId.PWCTR4);
            this.SendData(0x8A);
            this.SendData(0x2A);

            this.SendCommand(ST7735CommandId.PWCTR5);
            this.SendData(0x8A);
            this.SendData(0xEE);

            this.SendCommand(ST7735CommandId.VMCTR1);
            this.SendData(0x0E);

            this.SendCommand(ST7735CommandId.GAMCTRP1);
            this.SendData(0x0F);
            this.SendData(0x1A);
            this.SendData(0x0F);
            this.SendData(0x18);
            this.SendData(0x2F);
            this.SendData(0x28);
            this.SendData(0x20);
            this.SendData(0x22);
            this.SendData(0x1F);
            this.SendData(0x1B);
            this.SendData(0x23);
            this.SendData(0x37);
            this.SendData(0x00);
            this.SendData(0x07);
            this.SendData(0x02);
            this.SendData(0x10);

            this.SendCommand(ST7735CommandId.GAMCTRN1);
            this.SendData(0x0F);
            this.SendData(0x1B);
            this.SendData(0x0F);
            this.SendData(0x17);
            this.SendData(0x33);
            this.SendData(0x2C);
            this.SendData(0x29);
            this.SendData(0x2E);
            this.SendData(0x30);
            this.SendData(0x30);
            this.SendData(0x39);
            this.SendData(0x3F);
            this.SendData(0x00);
            this.SendData(0x07);
            this.SendData(0x03);
            this.SendData(0x10);
        }

        public void Dispose() {
            this.spi.Dispose();
            this.control.Dispose();
            this.reset?.Dispose();
        }

        public void Enable() => this.SendCommand(ST7735CommandId.DISPON);
        public void Disable() => this.SendCommand(ST7735CommandId.DISPOFF);

        private void SendCommand(ST7735CommandId command) {
            this.buffer1[0] = (byte)command;
            this.control.Write(GpioPinValue.Low);
            this.spi.Write(this.buffer1);
        }

        private void SendData(byte data) {
            this.buffer1[0] = data;
            this.control.Write(GpioPinValue.High);
            this.spi.Write(this.buffer1);
        }

        private void SendData(byte[] data) {
            this.control.Write(GpioPinValue.High);
            this.spi.Write(data);
        }

        public void SetDataAccessControl(bool swapRowColumn, bool invertRow, bool invertColumn, bool useBgrPanel) {
            var val = default(byte);

            if (useBgrPanel) val |= 0b0000_1000;
            if (swapRowColumn) val |= 0b0010_0000;
            if (invertColumn) val |= 0b0100_0000;
            if (invertRow) val |= 0b1000_0000;

            this.SendCommand(ST7735CommandId.MADCTL);
            this.SendData(val);

            this.rowColumnSwapped = swapRowColumn;
        }

        public void SetDataFormat(DataFormat dataFormat) {
            switch (dataFormat) {
                case DataFormat.Rgb444:
                    this.bpp = 12;
                    this.SendCommand(ST7735CommandId.COLMOD);
                    this.SendData(0x03);

                    break;

                case DataFormat.Rgb565:
                    this.bpp = 16;
                    this.SendCommand(ST7735CommandId.COLMOD);
                    this.SendData(0x05);

                    break;

                default:
                    throw new NotSupportedException();
            }

            this.DataFormat = dataFormat;
        }

        public void SetDrawWindow(int x, int y, int width, int height) {
            this.Width = width;
            this.Height = height;

            this.buffer4[1] = (byte)x;
            this.buffer4[3] = (byte)(x + width - 1);
            this.SendCommand(ST7735CommandId.CASET);
            this.SendData(this.buffer4);

            this.buffer4[1] = (byte)y;
            this.buffer4[3] = (byte)(y + height - 1);
            this.SendCommand(ST7735CommandId.RASET);
            this.SendData(this.buffer4);
        }

        private void SendDrawCommand() {
            this.SendCommand(ST7735CommandId.RAMWR);
            this.control.Write(GpioPinValue.High);
        }

        public void DrawBuffer(byte[] buffer) {
            this.SendDrawCommand();

            if (this.bpp == 16)
                BitConverter.SwapEndianness(buffer, 2);

            this.spi.Write(buffer);

            if (this.bpp == 16)
                BitConverter.SwapEndianness(buffer, 2);
        }

        public void DrawBuffer(byte[] buffer, int x, int y, int width, int height) {
            this.SetDrawWindow(x, y, width, height);

            this.DrawBuffer(buffer, x, y, width, height, this.MaxWidth, 1, 1);
        }

        public void DrawBuffer(byte[] buffer, int x, int y, int width, int height, int originalWidth, int columnMultiplier, int rowMultiplier) {
            if (this.bpp != 16)
                throw new NotSupportedException(); // Multiplier does suppport 16bbp only

            this.SendDrawCommand();

            BitConverter.SwapEndianness(buffer, 2);

            this.spi.Write(buffer, x, y, width, height, originalWidth, columnMultiplier, rowMultiplier);

            BitConverter.SwapEndianness(buffer, 2);
        }

        public void DrawBufferNative(byte[] buffer, int offset, int count) {
            this.SendDrawCommand();

            this.spi.Write(buffer, offset, count);
        }
    
    }
}
