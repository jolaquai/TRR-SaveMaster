using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using static System.Resources.ResXFileRef;
using static System.Windows.Forms.DataFormats;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TrackBar;

#pragma warning disable CA1416 // Validate platform compatibility

namespace TRR_SaveMaster;

internal sealed class TR4Utilities
{
    // Savegame constants & offsets
    private const int SLOT_STATUS_OFFSET = 0x004;
    private const int SAVE_NUMBER_OFFSET = 0x008;
    private const int GAME_MODE_OFFSET = 0x01C;
    private const int LEVEL_INDEX_OFFSET = 0x26F;
    private const int BASE_SAVEGAME_OFFSET_TR4 = 0x2000;
    private const int MAX_SAVEGAME_OFFSET_TR4 = 0x14AE00;
    private const int SAVEGAME_SIZE = 0xA470;
    private const int MAX_SAVEGAMES = 32;

    // Item offsets
    private const int GOLDEN_SKULLS_OFFSET = 0x1A6;
    private const int SMALL_MEDIPACK_OFFSET = 0x1BE;
    private const int LARGE_MEDIPACK_OFFSET = 0x1C0;
    private const int FLARES_OFFSET = 0x1C2;

    // Weapon offsets
    private const int PISTOLS_OFFSET = 0x194;
    private const int UZI_OFFSET = 0x195;
    private const int SHOTGUN_OFFSET = 0x196;
    private const int CROSSBOW_OFFSET = 0x197;
    private const int GRENADE_GUN_OFFSET = 0x199;
    private const int REVOLVER_OFFSET = 0x19A;

    // Ammo offsets
    private const int UZI_AMMO_OFFSET = 0x1C6;
    private const int REVOLVER_AMMO_OFFSET = 0x1C8;
    private const int SHOTGUN_NORMAL_AMMO_OFFSET = 0x1CA;
    private const int SHOTGUN_WIDESHOT_AMMO_OFFSET = 0x1CC;
    private const int GRENADE_GUN_NORMAL_AMMO_OFFSET = 0x1D0;
    private const int GRENADE_GUN_SUPER_AMMO_OFFSET = 0x1D2;
    private const int GRENADE_GUN_FLASH_AMMO_OFFSET = 0x1D4;
    private const int CROSSBOW_NORMAL_AMMO_OFFSET = 0x1D6;
    private const int CROSSBOW_POISON_AMMO_OFFSET = 0x1D8;
    private const int CROSSBOW_EXPLOSIVE_AMMO_OFFSET = 0x1DA;

    // Weapon byte flags
    private const byte WEAPON_PRESENT = 0x9;
    private const byte WEAPON_PRESENT_WITH_SIGHT = 0xD;

    // Health
    private const ushort MAX_HEALTH_VALUE = 1000;
    private const ushort MIN_HEALTH_VALUE = 1;
    private int MAX_HEALTH_OFFSET;
    private int MIN_HEALTH_OFFSET;
    private const byte FULL_HEALTH_TOGGLE_BYTE = 0x08;          // Toggle byte when health is full (not stored)
    private const byte PARTIAL_HEALTH_TOGGLE_BYTE = 0x0C;       // Toggle byte when health is partial (stored)
    private const byte TOGGLE_DELTA = 0x04;                     // Difference between full and partial toggle bytes

    // Misc
    private string savegamePath;
    private int savegameOffset;

    // Level names
    private static readonly string[] _levelNames =
    [
        null,
        "Angkor Wat",
        "Race for the Iris",
        "The Tomb of Seth",
        "Burial Chambers",
        "Valley of the Kings",
        "KV5",
        "Temple of Karnak",
        "The Great Hypostyle Hall",
        "Sacred Lake",
        "Tomb of Semerkhet",
        "Guardian of Semerkhet",
        "Desert Railroad",
        "Alexandria",
        "Coastal Ruins",
        "Pharos, Temple of Isis",
        "Cleopatra's Palaces",
        "Catacombs",
        "Temple of Poseidon",
        "The Lost Library",
        "Hall of Demetrius",
        "City of the Dead",
        "Trenches",
        "Chambers of Tulun",
        "Street Bazaar",
        "Citadel Gate",
        "Citadel",
        "The Sphinx Complex",
        "Underneath the Sphinx",
        "Menkaure's Pyramid",
        "Inside Menkaure's Pyramid",
        "The Mastabas",
        "The Great Pyramid",
        "Khufu's Queens Pyramids",
        "Inside the Great Pyramid",
        "Temple of Horus",
        "Temple of Horus",
        "The Times Office",
        "The Times Exclusive",
    ];

    private static void WriteInt32ToBuffer(byte[] buffer, int offset, int value)
    {
        Debug.Assert(buffer.Length >= offset + 4);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteUInt16ToBuffer(byte[] buffer, int offset, ushort value)
    {
        Debug.Assert(buffer.Length >= offset + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), value);
    }

    public (int Position, int ExtendedOffset) GetHealthOffset()
    {
        var savegameBuf = ArrayPool<byte>.Shared.Rent(SAVEGAME_SIZE);
        try
        {
            var savegameData = savegameBuf.AsSpan(0, SAVEGAME_SIZE);
            using (var fs = new FileStream(savegamePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                fs.Position = savegameOffset;
                fs.ReadExactly(savegameData);
            }

            var healthStateByteSearchBase = MIN_HEALTH_OFFSET - 23;
            var healthRange = savegameData[healthStateByteSearchBase..(MAX_HEALTH_OFFSET + 1)];

            // Attempt 1: Look for the partial health byte, see check for valid health after extended padding (23 instead of 19 bytes ahead)
            ref var ptr = ref MemoryMarshal.GetReference(healthRange);
            var length = healthRange.Length;
            List<int> candidateIndices = [];
            var offset = 0;

            var start = 0;
            var end = length;
            while (start < end)
            {
                var i = healthRange[start..].IndexOf(PARTIAL_HEALTH_TOGGLE_BYTE);
                if (i == -1)
                {
                    break;
                }

                // We could think about adding the known byte flag pattern check here as well, but tests so far have shown that this is pretty safe by itself

                offset = start + i + 23;
                var value = Unsafe.ReadUnaligned<ushort>(in Unsafe.Add(ref ptr, offset));
                if (value is (< MAX_HEALTH_VALUE and >= MIN_HEALTH_VALUE) or 0)
                {
                    candidateIndices.Add(offset);
                }
                start = offset + 1;
            }
            if (candidateIndices.Count == 1)
            {
                var finalOffset = savegameOffset + healthStateByteSearchBase + candidateIndices[0];
                return (finalOffset, 4);
            }

            // Attempt 2: If that didn't work, health may be full, so look for the full health byte instead
            // Since full health (1000) isn't stored though, we have nothing to go off of except the possible character animation byte patterns
            candidateIndices.Clear();

            start = 0;
            end = length;
            var extOffset = 0;
            while (start < end)
            {
                var i = healthRange[start..].IndexOf(FULL_HEALTH_TOGGLE_BYTE);
                if (i == -1)
                {
                    break;
                }
                var indexBase = start + i;

                try
                {
                    // In my tests, there were often 2 more bytes missing 4 bytes before the anim byte pattern, so we check both, it's just one extra read
                    // But if we're in here at all, we need to get back ahead of the pattern, but there may be extra movement flags
                    // Since full health is not stored, there can't be a valid health value there
                    offset = indexBase + 17;
                    var maybeFlag = Unsafe.ReadUnaligned<uint>(in Unsafe.Add(ref ptr, offset));
                    if (IsKnownByteFlagPattern(maybeFlag))
                    {
                        var tempOffset = offset + 4;
                        var maybeHealth = Unsafe.ReadUnaligned<ushort>(in Unsafe.Add(ref ptr, tempOffset));
                        if (maybeHealth == 0)
                        {
                            candidateIndices.Add(tempOffset);
                            extOffset = tempOffset - indexBase - 19;
                        }
                        continue;
                    }

                    offset += 2;
                    maybeFlag = Unsafe.ReadUnaligned<uint>(in Unsafe.Add(ref ptr, offset));
                    if (IsKnownByteFlagPattern(maybeFlag))
                    {
                        var tempOffset = offset + 4;
                        var maybeHealth = Unsafe.ReadUnaligned<ushort>(in Unsafe.Add(ref ptr, tempOffset));
                        if (maybeHealth == 0)
                        {
                            candidateIndices.Add(tempOffset);
                            extOffset = tempOffset - indexBase - 19;
                        }
                        continue;
                    }
                }
                finally
                {
                    // Make sure we continue searching
                    start = offset + 1;
                }
            }
            if (candidateIndices.Count == 1)
            {
                var finalOffset = savegameOffset + healthStateByteSearchBase + candidateIndices[0];
                // This works because we entered the ifs above exactly once if we're here
                return (finalOffset, extOffset);
            }

            // Attempt 3: If that also didn't work, fall back to the old method because it "just works" for all the default cases
            ptr = ref MemoryMarshal.GetReference(savegameData);
            var baseIdx = MIN_HEALTH_OFFSET;
            var endIdx = Math.Min(MAX_HEALTH_OFFSET, savegameData.Length - 2);

            for (var i = baseIdx; i <= endIdx; i++)
            {
                var value = Unsafe.ReadUnaligned<ushort>(in Unsafe.Add(ref ptr, i)); // BinaryPrimitives.ReadUInt16BigEndian(MemoryMarshal.CreateReadOnlySpan(in Unsafe.Add(ref ptr, i), 2));
                if (value is (>= MAX_HEALTH_VALUE or < MIN_HEALTH_VALUE) and not 0)
                {
                    continue;
                }

                var flagBase = i - 7;
                if ((uint)flagBase > (uint)(savegameData.Length - 4))
                {
                    continue;
                }

                var flag1 = Unsafe.ReadUnaligned<uint>(in Unsafe.Add(ref ptr, flagBase));
                if (!IsKnownByteFlagPattern(flag1))
                {
                    continue;
                }

                if (value != 0)
                {
                    return (savegameOffset + i, 0);
                }

                var toggleIdx = i - 0x13;
                if ((uint)toggleIdx < (uint)savegameData.Length && Unsafe.Add(ref ptr, toggleIdx) == FULL_HEALTH_TOGGLE_BYTE)
                {
                    return (savegameOffset + i, 0);
                }
            }

            return (-1, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(savegameBuf);
        }
    }

    public void DetermineOffsets(byte[] fileData)
    {
        var levelIndex = GetLevelIndex(fileData);

        switch (levelIndex)
        {
            case 1:
                MIN_HEALTH_OFFSET = 0x682;
                MAX_HEALTH_OFFSET = 0x684;
                break;
            case 2:
                MIN_HEALTH_OFFSET = 0xE5C;
                MAX_HEALTH_OFFSET = 0x192A;
                break;
            case 3:
                MIN_HEALTH_OFFSET = 0x7C4;
                MAX_HEALTH_OFFSET = 0x98A;
                break;
            case 4:
                MIN_HEALTH_OFFSET = 0x808;
                MAX_HEALTH_OFFSET = 0xE4D;
                break;
            case 5:
                MIN_HEALTH_OFFSET = 0x5F0;
                MAX_HEALTH_OFFSET = 0x1627;
                break;
            case 6:
                MIN_HEALTH_OFFSET = 0x94E;
                MAX_HEALTH_OFFSET = 0xF34;
                break;
            case 7:
                MIN_HEALTH_OFFSET = 0x612;
                MAX_HEALTH_OFFSET = 0x109F;
                break;
            case 8:
                MIN_HEALTH_OFFSET = 0xC6C;
                MAX_HEALTH_OFFSET = 0x1D56;
                break;
            case 9:
                MIN_HEALTH_OFFSET = 0x7EE;
                MAX_HEALTH_OFFSET = 0x1D1C;
                break;
            case 11:
                MIN_HEALTH_OFFSET = 0xDCD;
                MAX_HEALTH_OFFSET = 0x3116;
                break;
            case 12:
                MIN_HEALTH_OFFSET = 0x75F;
                MAX_HEALTH_OFFSET = 0x1E9D;
                break;
            case 13:
                MIN_HEALTH_OFFSET = 0x654;
                MAX_HEALTH_OFFSET = 0x6A2;
                break;
            case 14:
                MIN_HEALTH_OFFSET = 0x5DE;
                MAX_HEALTH_OFFSET = 0x3F6B;
                break;
            case 15:
                MIN_HEALTH_OFFSET = 0x978;
                MAX_HEALTH_OFFSET = 0x386D;
                break;
            case 16:
                MIN_HEALTH_OFFSET = 0x73A;
                MAX_HEALTH_OFFSET = 0x4BC2;
                break;
            case 17:
                MIN_HEALTH_OFFSET = 0x688;
                MAX_HEALTH_OFFSET = 0x5305;
                break;
            case 18:
                MIN_HEALTH_OFFSET = 0xE2B;
                MAX_HEALTH_OFFSET = 0x24F5;
                break;
            case 19:
                MIN_HEALTH_OFFSET = 0x8F3;
                MAX_HEALTH_OFFSET = 0x2B2E;
                break;
            case 20:
                MIN_HEALTH_OFFSET = 0xE7B;
                MAX_HEALTH_OFFSET = 0x3FE7;
                break;
            case 21:
                MIN_HEALTH_OFFSET = 0x5FA;
                MAX_HEALTH_OFFSET = 0x3E94;
                break;
            case 22:
                MIN_HEALTH_OFFSET = 0x651;
                MAX_HEALTH_OFFSET = 0x76A;
                break;
            case 23:
                MIN_HEALTH_OFFSET = 0xB74;
                MAX_HEALTH_OFFSET = 0x2645;
                break;
            case 24:
                MIN_HEALTH_OFFSET = 0xDAC;
                MAX_HEALTH_OFFSET = 0x1848;
                break;
            case 25:
                MIN_HEALTH_OFFSET = 0x6DD;
                MAX_HEALTH_OFFSET = 0x2736;
                break;
            case 26:
                MIN_HEALTH_OFFSET = 0x77A;
                MAX_HEALTH_OFFSET = 0x19CA;
                break;
            case 27:
                MIN_HEALTH_OFFSET = 0x8EA;
                MAX_HEALTH_OFFSET = 0x1070;
                break;
            case 28:
                MIN_HEALTH_OFFSET = 0x64B;
                MAX_HEALTH_OFFSET = 0x1880;
                break;
            case 30:
                MIN_HEALTH_OFFSET = 0x9C4;
                MAX_HEALTH_OFFSET = 0x1199;
                break;
            case 31:
                MIN_HEALTH_OFFSET = 0x5FC;
                MAX_HEALTH_OFFSET = 0x2307;
                break;
            case 32:
                MIN_HEALTH_OFFSET = 0x8BA;
                MAX_HEALTH_OFFSET = 0x2AE9;
                break;
            case 33:
                MIN_HEALTH_OFFSET = 0xA8C;
                MAX_HEALTH_OFFSET = 0x44AE;
                break;
            case 34:
                MIN_HEALTH_OFFSET = 0x69A;
                MAX_HEALTH_OFFSET = 0x500C;
                break;
            case 35:
                MIN_HEALTH_OFFSET = 0xA6B;
                MAX_HEALTH_OFFSET = 0x55F1;
                break;
            case 36:
                MIN_HEALTH_OFFSET = 0x612;
                MAX_HEALTH_OFFSET = 0x5D36;
                break;
            case 37:
                MIN_HEALTH_OFFSET = 0x88B;
                MAX_HEALTH_OFFSET = 0x79CF;
                break;
            case 38:
                MIN_HEALTH_OFFSET = 0x9ED;
                MAX_HEALTH_OFFSET = 0x8B04;
                break;
            case 40:
                MIN_HEALTH_OFFSET = 0x9F2;
                MAX_HEALTH_OFFSET = 0xFCD;
                break;
        }
    }

    private int GetSaveNumber(byte[] fileData)
    {
        return BitConverter.ToInt32(fileData, savegameOffset + SAVE_NUMBER_OFFSET);
    }

    private byte GetLevelIndex(byte[] fileData)
    {
        return fileData[savegameOffset + LEVEL_INDEX_OFFSET];
    }

    private ushort GetNumSmallMedipacks(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + SMALL_MEDIPACK_OFFSET);
    }

    private ushort GetNumLargeMedipacks(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + LARGE_MEDIPACK_OFFSET);
    }

    private ushort GetNumFlares(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + FLARES_OFFSET);
    }

    private sbyte GetNumGoldenSkulls(byte[] fileData)
    {
        return (sbyte)fileData[savegameOffset + GOLDEN_SKULLS_OFFSET];
    }

    private ushort GetUziAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + UZI_AMMO_OFFSET);
    }

    private ushort GetRevolverAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + REVOLVER_AMMO_OFFSET);
    }

    private ushort GetShotgunNormalAmmo(byte[] fileData)
    {
        return (ushort)(BitConverter.ToUInt16(fileData, savegameOffset + SHOTGUN_NORMAL_AMMO_OFFSET) / 6);
    }

    private ushort GetShotgunWideshotAmmo(byte[] fileData)
    {
        return (ushort)(BitConverter.ToUInt16(fileData, savegameOffset + SHOTGUN_WIDESHOT_AMMO_OFFSET) / 6);
    }

    private ushort GetGrenadeGunNormalAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + GRENADE_GUN_NORMAL_AMMO_OFFSET);
    }

    private ushort GetGrenadeGunSuperAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + GRENADE_GUN_SUPER_AMMO_OFFSET);
    }

    private ushort GetGrenadeGunFlashAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + GRENADE_GUN_FLASH_AMMO_OFFSET);
    }

    private ushort GetCrossbowNormalAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + CROSSBOW_NORMAL_AMMO_OFFSET);
    }

    private ushort GetCrossbowPoisonAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + CROSSBOW_POISON_AMMO_OFFSET);
    }

    private ushort GetCrossbowExplosiveAmmo(byte[] fileData)
    {
        return BitConverter.ToUInt16(fileData, savegameOffset + CROSSBOW_EXPLOSIVE_AMMO_OFFSET);
    }

    private byte GetRevolverFlag(byte[] fileData)
    {
        return fileData[savegameOffset + REVOLVER_OFFSET];
    }

    private byte GetCrossbowFlag(byte[] fileData)
    {
        return fileData[savegameOffset + CROSSBOW_OFFSET];
    }

    private static ushort GetHealthValue(byte[] fileData, int healthOffset)
    {
        var rawHealth = BitConverter.ToUInt16(fileData, healthOffset);

        if (rawHealth != 0)
        {
            return rawHealth;
        }

        return MAX_HEALTH_VALUE;
    }

    private bool IsPistolsPresent(byte[] fileData)
    {
        return fileData[savegameOffset + PISTOLS_OFFSET] != 0;
    }

    private bool IsUziPresent(byte[] fileData)
    {
        return fileData[savegameOffset + UZI_OFFSET] != 0;
    }

    private bool IsRevolverPresent(byte[] fileData)
    {
        return fileData[savegameOffset + REVOLVER_OFFSET] != 0;
    }

    private bool IsShotgunPresent(byte[] fileData)
    {
        return fileData[savegameOffset + SHOTGUN_OFFSET] != 0;
    }

    private bool IsGrenadeGunPresent(byte[] fileData)
    {
        return fileData[savegameOffset + GRENADE_GUN_OFFSET] != 0;
    }

    private bool IsCrossbowPresent(byte[] fileData)
    {
        return fileData[savegameOffset + CROSSBOW_OFFSET] != 0;
    }

    private void WriteSaveNumber(byte[] fileData, int value)
    {
        WriteInt32ToBuffer(fileData, savegameOffset + SAVE_NUMBER_OFFSET, value);
    }

    private void WriteNumSmallMedipacks(byte[] fileData, ushort value)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + SMALL_MEDIPACK_OFFSET, value);
    }

    private void WriteNumLargeMedipacks(byte[] fileData, ushort value)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + LARGE_MEDIPACK_OFFSET, value);
    }

    private void WriteNumFlares(byte[] fileData, ushort value)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + FLARES_OFFSET, value);
    }

    private void WriteNumGoldenSkulls(byte[] fileData, sbyte value)
    {
        fileData[savegameOffset + GOLDEN_SKULLS_OFFSET] = (byte)value;
    }

    private void WritePistolsPresent(byte[] fileData, bool isPresent)
    {
        if (isPresent)
        {
            fileData[savegameOffset + PISTOLS_OFFSET] = WEAPON_PRESENT;
        }
        else
        {
            fileData[savegameOffset + PISTOLS_OFFSET] = 0;
        }
    }

    private void WriteUziPresent(byte[] fileData, bool isPresent)
    {
        if (isPresent)
        {
            fileData[savegameOffset + UZI_OFFSET] = WEAPON_PRESENT;
        }
        else
        {
            fileData[savegameOffset + UZI_OFFSET] = 0;
        }
    }

    private void WriteRevolverPresent(byte[] fileData, bool isPresent, byte prevRevolverFlag)
    {
        if (isPresent && prevRevolverFlag != 0)
        {
            fileData[savegameOffset + REVOLVER_OFFSET] = prevRevolverFlag;
        }
        else if (isPresent)
        {
            fileData[savegameOffset + REVOLVER_OFFSET] = WEAPON_PRESENT_WITH_SIGHT;
        }
        else
        {
            fileData[savegameOffset + REVOLVER_OFFSET] = 0;
        }
    }

    private void WriteShotgunPresent(byte[] fileData, bool isPresent)
    {
        if (isPresent)
        {
            fileData[savegameOffset + SHOTGUN_OFFSET] = WEAPON_PRESENT;
        }
        else
        {
            fileData[savegameOffset + SHOTGUN_OFFSET] = 0;
        }
    }

    private void WriteGrenadeGunPresent(byte[] fileData, bool isPresent)
    {
        if (isPresent)
        {
            fileData[savegameOffset + GRENADE_GUN_OFFSET] = WEAPON_PRESENT;
        }
        else
        {
            fileData[savegameOffset + GRENADE_GUN_OFFSET] = 0;
        }
    }

    private void WriteCrossbowPresent(byte[] fileData, bool isPresent, byte prevCrossbowFlag)
    {
        if (isPresent && prevCrossbowFlag != 0)
        {
            fileData[savegameOffset + CROSSBOW_OFFSET] = prevCrossbowFlag;
        }
        else if (isPresent)
        {
            fileData[savegameOffset + CROSSBOW_OFFSET] = WEAPON_PRESENT_WITH_SIGHT;
        }
        else
        {
            fileData[savegameOffset + CROSSBOW_OFFSET] = 0;
        }
    }

    private void WriteUziAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + UZI_AMMO_OFFSET, ammo);
    }

    private void WriteRevolverAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + REVOLVER_AMMO_OFFSET, ammo);
    }

    private void WriteShotgunNormalAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + SHOTGUN_NORMAL_AMMO_OFFSET, ammo);
    }

    private void WriteShotgunWideshotAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + SHOTGUN_WIDESHOT_AMMO_OFFSET, ammo);
    }

    private void WriteGrenadeGunNormalAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + GRENADE_GUN_NORMAL_AMMO_OFFSET, ammo);
    }

    private void WriteGrenadeGunSuperAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + GRENADE_GUN_SUPER_AMMO_OFFSET, ammo);
    }

    private void WriteGrenadeGunFlashAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + GRENADE_GUN_FLASH_AMMO_OFFSET, ammo);
    }

    private void WriteCrossbowNormalAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + CROSSBOW_NORMAL_AMMO_OFFSET, ammo);
    }

    private void WriteCrossbowPoisonAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + CROSSBOW_POISON_AMMO_OFFSET, ammo);
    }

    private void WriteCrossbowExplosiveAmmo(byte[] fileData, ushort ammo)
    {
        WriteUInt16ToBuffer(fileData, savegameOffset + CROSSBOW_EXPLOSIVE_AMMO_OFFSET, ammo);
    }

    private void WriteHealthValue(byte[] fileData, ushort newHealth)
    {
        var (healthOffset, extendedOffset) = GetHealthOffset();

        if (healthOffset != -1)
        {
            var toggleOffset = healthOffset - 0x13 - extendedOffset;
            var currentToggle = fileData[toggleOffset];

            var currentlyFull = (currentToggle == FULL_HEALTH_TOGGLE_BYTE);
            var currentlyPartial = (currentToggle == PARTIAL_HEALTH_TOGGLE_BYTE);
            var newIsPartial = newHealth < MAX_HEALTH_VALUE;

            if (currentlyFull && newIsPartial)
            {
                // Full health -> Partial health
                fileData[toggleOffset] = (byte)(currentToggle + TOGGLE_DELTA);
                WriteUInt16ToBuffer(fileData, healthOffset, newHealth);
                ShiftBytesRight(ref fileData, healthOffset);
            }
            else if (currentlyPartial && !newIsPartial)
            {
                // Partial health -> Full health
                fileData[toggleOffset] = (byte)(currentToggle - TOGGLE_DELTA);
                WriteUInt16ToBuffer(fileData, healthOffset, 0);
                ShiftBytesLeft(ref fileData, healthOffset);
            }
            else if (currentlyFull && !newIsPartial)
            {
                // Already full health
                WriteUInt16ToBuffer(fileData, healthOffset, 0);
            }
            else
            {
                // Partial health -> Partial health
                WriteUInt16ToBuffer(fileData, healthOffset, newHealth);
            }
        }
    }

    private void ShiftBytesRight(ref byte[] fileData, int healthOffset)
    {
        var boundary = savegameOffset + SAVEGAME_SIZE;

        Array.Resize(ref fileData, fileData.Length + 2);

        for (var i = boundary - 1; i >= healthOffset + 2; i--)
        {
            fileData[i + 2] = fileData[i];
        }
    }

    private void ShiftBytesLeft(ref byte[] fileData, int healthOffset)
    {
        var boundary = savegameOffset + SAVEGAME_SIZE;

        for (var i = healthOffset + 2; i < boundary - 2; i++)
        {
            fileData[i] = fileData[i + 2];
        }

        Array.Resize(ref fileData, fileData.Length - 2);
    }

    public void DisplayGameInfo(byte[] fileData, NumericUpDown nudSaveNumber, NumericUpDown nudSmallMedipacks, NumericUpDown nudLargeMedipacks,
        NumericUpDown nudFlares, NumericUpDown nudGoldenSkulls, Label lblGoldenSkulls, CheckBox chkPistols, CheckBox chkShotgun, CheckBox chkUzi,
        CheckBox chkRevolver, CheckBox chkGrenadeGun, CheckBox chkCrossbow, TrackBar trbHealth, Label lblHealth,
        Label lblHealthError, NumericUpDown nudShotgunNormalAmmo, NumericUpDown nudShotgunWideshotAmmo, NumericUpDown nudUziAmmo,
        NumericUpDown nudRevolverAmmo, NumericUpDown nudCrossbowNormalAmmo, NumericUpDown nudGrenadeGunFlashAmmo,
        NumericUpDown nudGrenadeGunNormalAmmo, NumericUpDown nudGrenadeGunSuperAmmo, NumericUpDown nudCrossbowPoisonAmmo,
        NumericUpDown nudCrossbowExplosiveAmmo)
    {
        DetermineOffsets(fileData);

        nudSaveNumber.Value = GetSaveNumber(fileData);
        nudSmallMedipacks.Value = GetNumSmallMedipacks(fileData);
        nudLargeMedipacks.Value = GetNumLargeMedipacks(fileData);
        nudFlares.Value = GetNumFlares(fileData);

        chkPistols.Checked = IsPistolsPresent(fileData);
        chkUzi.Checked = IsUziPresent(fileData);
        chkShotgun.Checked = IsShotgunPresent(fileData);
        chkGrenadeGun.Checked = IsGrenadeGunPresent(fileData);
        chkCrossbow.Checked = IsCrossbowPresent(fileData);
        chkRevolver.Checked = IsRevolverPresent(fileData);

        nudUziAmmo.Value = GetUziAmmo(fileData);
        nudRevolverAmmo.Value = GetRevolverAmmo(fileData);
        nudShotgunNormalAmmo.Value = GetShotgunNormalAmmo(fileData);
        nudShotgunWideshotAmmo.Value = GetShotgunWideshotAmmo(fileData);
        nudCrossbowNormalAmmo.Value = GetCrossbowNormalAmmo(fileData);
        nudCrossbowPoisonAmmo.Value = GetCrossbowPoisonAmmo(fileData);
        nudCrossbowExplosiveAmmo.Value = GetCrossbowExplosiveAmmo(fileData);
        nudGrenadeGunNormalAmmo.Value = GetGrenadeGunNormalAmmo(fileData);
        nudGrenadeGunSuperAmmo.Value = GetGrenadeGunSuperAmmo(fileData);
        nudGrenadeGunFlashAmmo.Value = GetGrenadeGunFlashAmmo(fileData);

        var levelIndex = GetLevelIndex(fileData);

        if (levelIndex == 1 || levelIndex == 2) // Angkor Wat and Race for the Iris
        {
            lblGoldenSkulls.Visible = true;
            nudGoldenSkulls.Visible = true;
            nudGoldenSkulls.Enabled = true;
            nudGoldenSkulls.Value = GetNumGoldenSkulls(fileData);
        }
        else
        {
            lblGoldenSkulls.Visible = false;
            nudGoldenSkulls.Visible = false;
            nudGoldenSkulls.Enabled = false;
            nudGoldenSkulls.Value = 0;
        }

        var (healthOffset, _) = GetHealthOffset();

        if (healthOffset != -1)
        {
            var health = GetHealthValue(fileData, healthOffset);
            var healthPercentage = ((double)health / MAX_HEALTH_VALUE) * 100;
            trbHealth.Value = health;
            trbHealth.Enabled = true;

            lblHealth.Text = healthPercentage.ToString("0.0") + "%";
            lblHealthError.Visible = false;
            lblHealth.Visible = true;
        }
        else
        {
            trbHealth.Enabled = false;
            trbHealth.Value = trbHealth.Minimum;
            lblHealthError.Visible = true;
            lblHealth.Visible = false;
        }
    }

    public void WriteChanges(byte[] fileData, NumericUpDown nudSaveNumber, NumericUpDown nudGoldenSkulls, NumericUpDown nudSmallMedipacks,
        NumericUpDown nudLargeMedipacks, NumericUpDown nudFlares, CheckBox chkPistols, CheckBox chkUzi, CheckBox chkRevolver,
        CheckBox chkShotgun, CheckBox chkGrenadeGun, CheckBox chkCrossbow, NumericUpDown nudUziAmmo, NumericUpDown nudRevolverAmmo,
        NumericUpDown nudShotgunNormalAmmo, NumericUpDown nudShotgunWideshotAmmo, NumericUpDown nudGrenadeGunNormalAmmo, NumericUpDown nudGrenadeGunSuperAmmo,
        NumericUpDown nudGrenadeGunFlashAmmo, NumericUpDown nudCrossbowNormalAmmo, NumericUpDown nudCrossbowPoisonAmmo, NumericUpDown nudCrossbowExplosiveAmmo,
        TrackBar trbHealth)
    {
        var prevCrossbowFlag = GetCrossbowFlag(fileData);
        var prevRevolverFlag = GetRevolverFlag(fileData);

        WriteSaveNumber(fileData, (int)nudSaveNumber.Value);
        WriteNumSmallMedipacks(fileData, (ushort)nudSmallMedipacks.Value);
        WriteNumLargeMedipacks(fileData, (ushort)nudLargeMedipacks.Value);
        WriteNumFlares(fileData, (ushort)nudFlares.Value);

        WritePistolsPresent(fileData, chkPistols.Checked);
        WriteUziPresent(fileData, chkUzi.Checked);
        WriteShotgunPresent(fileData, chkShotgun.Checked);
        WriteGrenadeGunPresent(fileData, chkGrenadeGun.Checked);
        WriteCrossbowPresent(fileData, chkCrossbow.Checked, prevCrossbowFlag);
        WriteRevolverPresent(fileData, chkRevolver.Checked, prevRevolverFlag);

        WriteUziAmmo(fileData, (ushort)nudUziAmmo.Value);
        WriteRevolverAmmo(fileData, (ushort)nudRevolverAmmo.Value);
        WriteShotgunNormalAmmo(fileData, (ushort)(nudShotgunNormalAmmo.Value * 6));
        WriteShotgunWideshotAmmo(fileData, (ushort)(nudShotgunWideshotAmmo.Value * 6));
        WriteCrossbowNormalAmmo(fileData, (ushort)nudCrossbowNormalAmmo.Value);
        WriteCrossbowPoisonAmmo(fileData, (ushort)nudCrossbowPoisonAmmo.Value);
        WriteCrossbowExplosiveAmmo(fileData, (ushort)nudCrossbowExplosiveAmmo.Value);
        WriteGrenadeGunNormalAmmo(fileData, (ushort)nudGrenadeGunNormalAmmo.Value);
        WriteGrenadeGunSuperAmmo(fileData, (ushort)nudGrenadeGunSuperAmmo.Value);
        WriteGrenadeGunFlashAmmo(fileData, (ushort)nudGrenadeGunFlashAmmo.Value);

        if (nudGoldenSkulls.Enabled)
        {
            WriteNumGoldenSkulls(fileData, (sbyte)nudGoldenSkulls.Value);
        }

        if (trbHealth.Enabled)
        {
            WriteHealthValue(fileData, (ushort)trbHealth.Value);
        }

        File.WriteAllBytes(savegamePath, fileData);
    }

    // known flag consts pre-swapped from BE to LE for faster searches
    private static readonly FrozenSet<uint> _vehicleFlags = FrozenSet.ToFrozenSet([
        BinaryPrimitives.ReverseEndianness(0x00000040u), // Jeep
        BinaryPrimitives.ReverseEndianness(0x01010034u), // Jeep
        BinaryPrimitives.ReverseEndianness(0x0B0B0038u), // Jeep
        BinaryPrimitives.ReverseEndianness(0x0B0C0038u), // Jeep
        BinaryPrimitives.ReverseEndianness(0x0C0C473Au), // Jeep
        BinaryPrimitives.ReverseEndianness(0x00474740u), // Jeep
        BinaryPrimitives.ReverseEndianness(0x0F0F0034u), // Motorbike
        BinaryPrimitives.ReverseEndianness(0x01010024u), // Motorbike
        BinaryPrimitives.ReverseEndianness(0x08080038u), // Motorbike
        BinaryPrimitives.ReverseEndianness(0x08080039u), // Motorbike
        BinaryPrimitives.ReverseEndianness(0x1111003Au), // Motorbike
        BinaryPrimitives.ReverseEndianness(0x01010027u), // Motorbike
    ]);
    // ======================================== EXPERIMENTAL ========================================
    private static readonly FrozenSet<uint> _freefallFlags = Enumerable.Sequence(0x1700B001u, 0x1700BF01u, 1u << 8).Select(BinaryPrimitives.ReverseEndianness).ToFrozenSet();
    // ======================================== EXPERIMENTAL ========================================
    private static readonly FrozenSet<uint> _knownFlags = FrozenSet.ToFrozenSet([
        BinaryPrimitives.ReverseEndianness(0x02020052u), // Standing
        BinaryPrimitives.ReverseEndianness(0x02020067u), // Standing
        BinaryPrimitives.ReverseEndianness(0x02024767u), // Standing
        BinaryPrimitives.ReverseEndianness(0x50500007u), // Crawling
        BinaryPrimitives.ReverseEndianness(0x50504707u), // Crawling
        BinaryPrimitives.ReverseEndianness(0x474700DEu), // Crouching
        BinaryPrimitives.ReverseEndianness(0x01010006u), // Running forward
        BinaryPrimitives.ReverseEndianness(0x010100F4u), // Sprinting
        BinaryPrimitives.ReverseEndianness(0x0303004Du), // Jumping forward
        BinaryPrimitives.ReverseEndianness(0x17020093u), // Rolling
        BinaryPrimitives.ReverseEndianness(0x13130061u), // Climbing
        BinaryPrimitives.ReverseEndianness(0x2A000083u), // Using puzzle item
        BinaryPrimitives.ReverseEndianness(0x2B000086u), // Using puzzle item
        BinaryPrimitives.ReverseEndianness(0x2121006Eu), // On water
        BinaryPrimitives.ReverseEndianness(0x21210075u), // Wading through water
        BinaryPrimitives.ReverseEndianness(0x0D0D006Cu), // Underwater
        BinaryPrimitives.ReverseEndianness(0x0D12006Cu), // Underwater
        BinaryPrimitives.ReverseEndianness(0x121200C6u), // Swimming forward
        BinaryPrimitives.ReverseEndianness(0x120D00C8u), // Swimming forward
        BinaryPrimitives.ReverseEndianness(0x18180046u), // Sliding downhill

        .._freefallFlags,

        .._vehicleFlags,
    ]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static bool IsKnownByteFlagPattern(uint packed) => _knownFlags.Contains(packed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLaraInVehicle(int healthOffset, byte[] fileData)
    {
        var packed = Unsafe.ReadUnaligned<uint>(in fileData[healthOffset - 7]);
        return _vehicleFlags.Contains(packed);
    }

    public void SetSavegamePath(string path)
    {
        savegamePath = path;
    }

    public void SetSavegameOffset(int offset)
    {
        savegameOffset = offset;
    }

    public bool IsSavegamePresent(byte[] fileData)
    {
        return fileData[savegameOffset + SLOT_STATUS_OFFSET] != 0;
    }

    public static void UpdateDisplayName(Savegame savegame, byte[] fileData)
    {
        var savegamePresent = fileData[savegame.Offset + SLOT_STATUS_OFFSET] != 0;

        if (savegamePresent)
        {
            var levelIndex = fileData[savegame.Offset + LEVEL_INDEX_OFFSET];
            var saveNumber = BitConverter.ToInt32(fileData, savegame.Offset + SAVE_NUMBER_OFFSET);

            if (levelIndex is > 0 && levelIndex < _levelNames.Length && saveNumber >= 0)
            {
                var levelName = _levelNames[levelIndex];
                var gameMode = fileData[savegame.Offset + GAME_MODE_OFFSET] == 0 ? GameMode.Normal : GameMode.Plus;

                savegame.UpdateDisplayName(levelName, saveNumber, gameMode);
            }
        }
    }

    public void PopulateEmptySlots(ComboBox cmbSavegames)
    {
        if (cmbSavegames.Items.Count == MAX_SAVEGAMES)
        {
            return;
        }

        var fileData = File.ReadAllBytes(savegamePath);

        for (var i = cmbSavegames.Items.Count; i < MAX_SAVEGAMES; i++)
        {
            var currentSavegameOffset = BASE_SAVEGAME_OFFSET_TR4 + (i * SAVEGAME_SIZE);

            if (currentSavegameOffset < MAX_SAVEGAME_OFFSET_TR4)
            {
                var slotStatus = fileData[currentSavegameOffset + SLOT_STATUS_OFFSET];
                var levelIndex = fileData[currentSavegameOffset + LEVEL_INDEX_OFFSET];
                var saveNumber = BitConverter.ToInt32(fileData, currentSavegameOffset + SAVE_NUMBER_OFFSET);
                var savegamePresent = slotStatus != 0;

                if (savegamePresent && levelIndex is > 0 && levelIndex < _levelNames.Length && saveNumber >= 0)
                {
                    var slot = (currentSavegameOffset - BASE_SAVEGAME_OFFSET_TR4) / SAVEGAME_SIZE;

                    var savegameExists = false;

                    foreach (Savegame existingSavegame in cmbSavegames.Items)
                    {
                        if (existingSavegame.Slot == slot)
                        {
                            savegameExists = true;
                            break;
                        }
                    }

                    if (!savegameExists)
                    {
                        var levelName = _levelNames[levelIndex];
                        var gameMode = fileData[currentSavegameOffset + GAME_MODE_OFFSET] == 0 ? GameMode.Normal : GameMode.Plus;

                        var savegame = new Savegame(currentSavegameOffset, slot, saveNumber, levelName, gameMode);
                        cmbSavegames.Items.Add(savegame);
                    }
                }
            }
        }
    }

    public void PopulateSavegames(ComboBox cmbSavegames)
    {
        var fileData = File.ReadAllBytes(savegamePath);
        var numSaves = 0;

        for (var i = 0; i < MAX_SAVEGAMES; i++)
        {
            var currentSavegameOffset = BASE_SAVEGAME_OFFSET_TR4 + (i * SAVEGAME_SIZE);

            var slotStatus = fileData[currentSavegameOffset + SLOT_STATUS_OFFSET];
            var levelIndex = fileData[currentSavegameOffset + LEVEL_INDEX_OFFSET];
            var saveNumber = BitConverter.ToInt32(fileData, currentSavegameOffset + SAVE_NUMBER_OFFSET);
            var savegamePresent = slotStatus != 0;

            if (savegamePresent && levelIndex is > 0 && levelIndex < _levelNames.Length && saveNumber >= 0)
            {
                var levelName = _levelNames[levelIndex];
                var slot = (currentSavegameOffset - BASE_SAVEGAME_OFFSET_TR4) / SAVEGAME_SIZE;
                var gameMode = fileData[currentSavegameOffset + GAME_MODE_OFFSET] == 0 ? GameMode.Normal : GameMode.Plus;

                var savegame = new Savegame(currentSavegameOffset, slot, saveNumber, levelName, gameMode);
                cmbSavegames.Items.Add(savegame);

                numSaves++;
            }
        }

        if (numSaves > 0)
        {
            cmbSavegames.SelectedIndex = 0;
        }
    }
}
