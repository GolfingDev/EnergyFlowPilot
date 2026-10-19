using System.Buffers.Binary;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Dal.Tests;

public sealed class WeishauptBiblockModbusProtocolTests
{
    [Fact]
    public void CreateReadInputRegisterRequestUsesOnlyFunctionCode04()
    {
        var request = WeishauptBiblockModbusProtocol.CreateReadInputRegisterRequest(3, 1, 2101);

        Assert.Equal(12, request.Length);
        Assert.Equal(0x04, request[7]);
        Assert.Equal((ushort)2101, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
    }

    [Fact]
    public void ParseSingleInputRegisterResponseReturnsUnsignedRegisterValue()
    {
        var response = new byte[] { 0, 3, 0, 0, 0, 5, 1, 0x04, 2, 1, 194 };

        var value = WeishauptBiblockModbusProtocol.ParseSingleInputRegisterResponse(response, 3, 1);

        Assert.Equal((ushort)450, value);
    }
}
