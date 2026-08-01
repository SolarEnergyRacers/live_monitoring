using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

public class CANFrameDecoderTests
{
    private readonly CANFrameDecoder _decoder = new();

    // Mppt1Addr = 0x600, Mppt2Addr = 0x610, Mppt3Addr = 0x620, mask 0xFF0 => each matches its own 16-address block
    [Theory]
    [InlineData(0x600)]
    [InlineData(0x605)]
    [InlineData(0x60F)]
    [InlineData(0x610)]
    [InlineData(0x615)]
    [InlineData(0x61F)]
    [InlineData(0x620)]
    [InlineData(0x625)]
    [InlineData(0x62F)]
    public void IsMpptFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsMpptFrame(addr));
    }

    [Theory]
    [InlineData(0x5FF)]
    [InlineData(0x630)]
    [InlineData(0x700)]
    [InlineData(0)]
    public void IsMpptFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsMpptFrame(addr));
    }

    // BmsBaseAddr = 0x700, mask 0xF00 => matches 0x700-0x7FF
    [Theory]
    [InlineData(0x700)]
    [InlineData(0x750)]
    [InlineData(0x7FF)]
    public void IsBmsFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsBmsFrame(addr));
    }

    [Theory]
    [InlineData(0x6FF)]
    [InlineData(0x800)]
    [InlineData(0x600)]
    [InlineData(0)]
    public void IsBmsFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsBmsFrame(addr));
    }

    // AcBaseAddr = 0x630, mask 0xFF0 => matches 0x630-0x63F
    [Theory]
    [InlineData(0x630)]
    [InlineData(0x635)]
    [InlineData(0x63F)]
    public void IsAcFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsAcFrame(addr));
    }

    [Theory]
    [InlineData(0x62F)]
    [InlineData(0x640)]
    [InlineData(0x660)]
    [InlineData(0)]
    public void IsAcFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsAcFrame(addr));
    }

    // DcBaseAddr = 0x660, mask 0xFF0 => matches 0x660-0x66F
    [Theory]
    [InlineData(0x660)]
    [InlineData(0x665)]
    [InlineData(0x66F)]
    public void IsDcFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsDcFrame(addr));
    }

    [Theory]
    [InlineData(0x65F)]
    [InlineData(0x670)]
    [InlineData(0x630)]
    [InlineData(0)]
    public void IsDcFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsDcFrame(addr));
    }

    // McBaseAddr = 0x500, mask 0xF00 => matches 0x500-0x5FF
    [Theory]
    [InlineData(0x500)]
    [InlineData(0x580)]
    [InlineData(0x5FF)]
    public void IsMcFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsMcFrame(addr));
    }

    [Theory]
    [InlineData(0x4FF)]
    [InlineData(0x600)]
    [InlineData(0x700)]
    [InlineData(0)]
    public void IsMcFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsMcFrame(addr));
    }

    // Each base address should be recognized only by its own Is*Frame method.
    [Theory]
    [InlineData(0x600)]
    [InlineData(0x700)]
    [InlineData(0x630)]
    [InlineData(0x660)]
    [InlineData(0x500)]
    public void IsFrameMethods_AreMutuallyExclusive(short baseAddr)
    {
        bool[] results =
        [
            _decoder.IsMpptFrame(baseAddr),
            _decoder.IsBmsFrame(baseAddr),
            _decoder.IsAcFrame(baseAddr),
            _decoder.IsDcFrame(baseAddr),
            _decoder.IsMcFrame(baseAddr),
        ];

        Assert.Single(results, r => r);
    }
}
