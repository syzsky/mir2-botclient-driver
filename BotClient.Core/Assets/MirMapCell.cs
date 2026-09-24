namespace BotClient.Assets;

public readonly record struct MirMapCell(
    ushort BkImg, ushort MidImg, ushort FrImg,
    byte DoorIndex, byte DoorOffset, byte AniFrame, byte AniTick,
    byte Area, byte Light, byte Tiles, byte SmTiles,
    ushort BkImg2, ushort MidImg2, ushort FrImg2,
    byte DoorIndex2, byte DoorOffset2, ushort AniFrame2, byte Area2, byte Light2,
    byte Tiles2, byte SmTiles2,
    byte Temp0, byte Temp1, byte Temp2, byte Temp3, byte Temp4, byte Temp5, byte Temp6, byte Temp7)
{
    public bool IsWalkable => (BkImg & 0x8000) == 0 && (FrImg & 0x8000) == 0;

    internal static MirMapCell Parse(ReadOnlySpan<byte> data, MirMapFormat format)
    {
        ushort bk = BitConverter.ToUInt16(data);
        ushort mid = BitConverter.ToUInt16(data[2..]);
        ushort fr = BitConverter.ToUInt16(data[4..]);
        byte tiles = 0, smTiles = 0;
        ushort bk2 = 0, mid2 = 0, fr2 = 0, aniFrame2 = 0;
        byte doorIndex2 = 0, doorOffset2 = 0, area2 = 0, light2 = 0;
        byte tiles2 = 0, smTiles2 = 0;
        byte t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0, t6 = 0, t7 = 0;

        if (format is MirMapFormat.V2 or MirMapFormat.V6)
        {
            tiles = data[12]; smTiles = data[13];
        }
        if (format == MirMapFormat.V6)
        {
            bk2 = BitConverter.ToUInt16(data[14..]); mid2 = BitConverter.ToUInt16(data[16..]);
            fr2 = BitConverter.ToUInt16(data[18..]); doorIndex2 = data[20]; doorOffset2 = data[21];
            aniFrame2 = BitConverter.ToUInt16(data[22..]); area2 = data[24]; light2 = data[25];
            tiles2 = data[26]; smTiles2 = data[27]; t0 = data[28]; t1 = data[29];
            t2 = data[30]; t3 = data[31]; t4 = data[32]; t5 = data[33]; t6 = data[34]; t7 = data[35];
        }

        return new MirMapCell(bk, mid, fr, data[6], data[7], data[8], data[9],
            data[10], data[11], tiles, smTiles,
            bk2, mid2, fr2, doorIndex2, doorOffset2, aniFrame2, area2, light2,
            tiles2, smTiles2, t0, t1, t2, t3, t4, t5, t6, t7);
    }
}
