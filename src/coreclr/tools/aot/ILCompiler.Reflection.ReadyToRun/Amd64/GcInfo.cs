// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ILCompiler.Reflection.ReadyToRun.Amd64
{
    public class GcInfo : BaseGcInfo
    {
        /// <summary>
        /// based on <a href="https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/gcinfodecoder.h">src/inc/gcinfodecoder.h</a> GcInfoHeaderFlags
        /// </summary>
        private enum GcInfoHeaderFlags
        {
            GC_INFO_IS_VARARG = 0x1,
            GC_INFO_HAS_SECURITY_OBJECT = 0x2,
            GC_INFO_HAS_GS_COOKIE = 0x4,
            GC_INFO_HAS_PSP_SYM = 0x8,
            GC_INFO_HAS_GENERICS_INST_CONTEXT_MASK = 0x30,
            GC_INFO_HAS_GENERICS_INST_CONTEXT_NONE = 0x00,
            GC_INFO_HAS_GENERICS_INST_CONTEXT_MT = 0x10,
            GC_INFO_HAS_GENERICS_INST_CONTEXT_MD = 0x20,
            GC_INFO_HAS_GENERICS_INST_CONTEXT_THIS = 0x30,
            GC_INFO_HAS_STACK_BASE_REGISTER = 0x40,
            GC_INFO_WANTS_REPORT_ONLY_LEAF = 0x80, // GC_INFO_HAS_TAILCALLS = 0x80, for ARM and ARM64
            GC_INFO_HAS_EDIT_AND_CONTINUE_PRESERVED_SLOTS = 0x100,
            GC_INFO_REVERSE_PINVOKE_FRAME = 0x200,

            GC_INFO_FLAGS_BIT_SIZE_VERSION_1 = 9,
            GC_INFO_FLAGS_BIT_SIZE = 10,
        };

        public struct SafePointOffset
        {
            public int Index { get; set; }
            public uint Value { get; set; }
            public SafePointOffset(int index, uint value)
            {
                Index = index;
                Value = value;
            }
        }

        private const int MIN_GCINFO_VERSION_WITH_RETURN_KIND = 2;
        private const int MAX_GCINFO_VERSION_WITH_RETURN_KIND = 3;
        private const int MIN_GCINFO_VERSION_WITH_REV_PINVOKE_FRAME = 2;
        private const int MIN_GCINFO_VERSION_WITH_NORMALIZED_CODE_OFFSETS = 3;

        private bool _slimHeader;
        private bool _hasSecurityObject;
        private bool _hasGSCookie;
        private bool _hasPSPSym;
        private bool _hasGenericsInstContext;
        private bool _hasStackBaseRegister;
        private bool _hasSizeOfEditAndContinuePreservedArea;
        private bool _hasReversePInvokeFrame;
        private bool _wantsReportOnlyLeaf;

        private Machine _machine;
        private GcInfoTypes _gcInfoTypes;

        public int Version { get; set; }
        public ReturnKinds ReturnKind { get; set; }
        public uint ValidRangeStart { get; set; }
        public uint ValidRangeEnd { get; set; }
        public int SecurityObjectStackSlot { get; set; }
        public int GSCookieStackSlot { get; set; }
        public int PSPSymStackSlot { get; set; }
        public int GenericsInstContextStackSlot { get; set; }
        public uint StackBaseRegister { get; set; }
        public uint SizeOfEditAndContinuePreservedArea { get; set; }
        public int ReversePInvokeFrameStackSlot { get; set; }
        public uint SizeOfStackOutgoingAndScratchArea { get; set; }
        public uint NumSafePoints { get; set; }
        public uint NumInterruptibleRanges { get; set; }
        public List<SafePointOffset> SafePointOffsets { get; set; }
        public List<InterruptibleRange> InterruptibleRanges { get; set; }
        public GcSlotTable SlotTable { get; set; }

        public GcInfo() { }

        /// <summary>
        /// based on <a href="https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/gcinfodecoder.cpp">GcInfoDecoder::GcInfoDecoder</a>
        /// </summary>
        public GcInfo(NativeReader imageReader, int offset, Machine machine, ushort majorVersion, ushort minorVersion)
        {
            Offset = offset;
            Version = ReadyToRunVersionToGcInfoVersion(majorVersion, minorVersion);
            bool denormalizeCodeOffsets = Version > MIN_GCINFO_VERSION_WITH_NORMALIZED_CODE_OFFSETS;
            _gcInfoTypes = new GcInfoTypes(machine, denormalizeCodeOffsets);
            _machine = machine;

            SecurityObjectStackSlot = -1;
            GSCookieStackSlot = -1;
            PSPSymStackSlot = -1;
            SecurityObjectStackSlot = -1;
            GenericsInstContextStackSlot = -1;
            StackBaseRegister = 0xffffffff;
            SizeOfEditAndContinuePreservedArea = 0xffffffff;
            ReversePInvokeFrameStackSlot = -1;

            int bitOffset = offset * 8;

            ParseHeaderFlags(imageReader, ref bitOffset);

            if (Version >= MIN_GCINFO_VERSION_WITH_RETURN_KIND && Version <= MAX_GCINFO_VERSION_WITH_RETURN_KIND) // IsReturnKindAvailable
            {
                int returnKindBits = (_slimHeader) ? _gcInfoTypes.SIZE_OF_RETURN_KIND_SLIM : _gcInfoTypes.SIZE_OF_RETURN_KIND_FAT;
                ReturnKind = (ReturnKinds)imageReader.ReadBits(returnKindBits, ref bitOffset);
            }

            CodeLength = _gcInfoTypes.DenormalizeCodeLength((int)imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.CODE_LENGTH_ENCBASE, ref bitOffset));

            if (_hasGSCookie)
            {
                uint normPrologSize = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.NORM_PROLOG_SIZE_ENCBASE, ref bitOffset) + 1;
                uint normEpilogSize = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.NORM_EPILOG_SIZE_ENCBASE, ref bitOffset);

                ValidRangeStart = _gcInfoTypes.DenormalizeCodeOffset(normPrologSize);
                ValidRangeEnd = (uint)CodeLength - _gcInfoTypes.DenormalizeCodeOffset(normEpilogSize);
            }
            else if (_hasSecurityObject || _hasGenericsInstContext)
            {
                uint normValidRangeStart = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.NORM_PROLOG_SIZE_ENCBASE, ref bitOffset) + 1;
                ValidRangeStart = _gcInfoTypes.DenormalizeCodeOffset(normValidRangeStart);
                ValidRangeEnd = ValidRangeStart + 1;
            }

            if (_hasSecurityObject)
            {
                SecurityObjectStackSlot = _gcInfoTypes.DenormalizeStackSlot(imageReader.DecodeVarLengthSigned(_gcInfoTypes.SECURITY_OBJECT_STACK_SLOT_ENCBASE, ref bitOffset));
            }

            if (_hasGSCookie)
            {
                GSCookieStackSlot = _gcInfoTypes.DenormalizeStackSlot(imageReader.DecodeVarLengthSigned(_gcInfoTypes.GS_COOKIE_STACK_SLOT_ENCBASE, ref bitOffset));
            }

            if (_hasPSPSym)
            {
                PSPSymStackSlot = _gcInfoTypes.DenormalizeStackSlot(imageReader.DecodeVarLengthSigned(_gcInfoTypes.PSP_SYM_STACK_SLOT_ENCBASE, ref bitOffset));
            }

            if (_hasGenericsInstContext)
            {
                GenericsInstContextStackSlot = _gcInfoTypes.DenormalizeStackSlot(imageReader.DecodeVarLengthSigned(_gcInfoTypes.GENERICS_INST_CONTEXT_STACK_SLOT_ENCBASE, ref bitOffset));
            }

            if (_hasStackBaseRegister && !_slimHeader)
            {
                StackBaseRegister = _gcInfoTypes.DenormalizeStackBaseRegister(imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.STACK_BASE_REGISTER_ENCBASE, ref bitOffset));
            }

            if (_hasSizeOfEditAndContinuePreservedArea)
            {
                SizeOfEditAndContinuePreservedArea = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.SIZE_OF_EDIT_AND_CONTINUE_PRESERVED_AREA_ENCBASE, ref bitOffset);
            }

            if (_hasReversePInvokeFrame)
            {
                ReversePInvokeFrameStackSlot = imageReader.DecodeVarLengthSigned(_gcInfoTypes.REVERSE_PINVOKE_FRAME_ENCBASE, ref bitOffset);
            }

            // FIXED_STACK_PARAMETER_SCRATCH_AREA (this macro is always defined in _gcInfoTypes.h)
            if (!_slimHeader)
            {
                SizeOfStackOutgoingAndScratchArea = _gcInfoTypes.DenormalizeSizeOfStackArea(imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.SIZE_OF_STACK_AREA_ENCBASE, ref bitOffset));
            }

            // PARTIALLY_INTERRUPTIBLE_GC_SUPPORTED (this macro is always defined in _gcInfoTypes.h)
            NumSafePoints = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.NUM_SAFE_POINTS_ENCBASE, ref bitOffset);

            if (!_slimHeader)
            {
                NumInterruptibleRanges = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.NUM_INTERRUPTIBLE_RANGES_ENCBASE, ref bitOffset);
            }

            // PARTIALLY_INTERRUPTIBLE_GC_SUPPORTED (this macro is always defined in _gcInfoTypes.h)
            SafePointOffsets = EnumerateSafePoints(imageReader, ref bitOffset);

            InterruptibleRanges = EnumerateInterruptibleRanges(imageReader, _gcInfoTypes.INTERRUPTIBLE_RANGE_DELTA1_ENCBASE, _gcInfoTypes.INTERRUPTIBLE_RANGE_DELTA2_ENCBASE, ref bitOffset);

            SlotTable = new GcSlotTable(imageReader, machine, _gcInfoTypes, ref bitOffset);

            if (SlotTable.NumSlots > 0)
            {
                if (NumSafePoints > 0)
                {
                    // Partially interruptible code
                    LiveSlotsAtSafepoints = GetLiveSlotsAtSafepoints(imageReader, ref bitOffset);
                }
                else
                {
                    // Fully interruptible code
                    Transitions = GetTransitions(imageReader, ref bitOffset);
                }
            }

            int nextByteOffset = (bitOffset + 7) >> 3;
            Size = nextByteOffset - offset;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"    Version: {Version}");
            sb.AppendLine($"    CodeLength: {CodeLength}");
            sb.AppendLine($"    ReturnKind: {Enum.GetName(typeof(ReturnKinds), ReturnKind)}");
            sb.AppendLine($"    ValidRangeStart: {ValidRangeStart}");
            sb.AppendLine($"    ValidRangeEnd: {ValidRangeEnd}");
            if (SecurityObjectStackSlot != -1)
                sb.AppendLine($"    SecurityObjectStackSlot: caller.sp{SecurityObjectStackSlot:+#;-#;+0}");

            if (GSCookieStackSlot != -1)
            {
                sb.AppendLine($"    GSCookieStackSlot: caller.sp{GSCookieStackSlot:+#;-#;+0}");
                sb.AppendLine($"    GS cookie valid range: [{ValidRangeStart};{ValidRangeEnd})");
            }

            if (PSPSymStackSlot != -1)
            {
                if (_machine == Machine.Amd64)
                {
                    sb.AppendLine($"    PSPSymStackSlot: initial.sp{PSPSymStackSlot:+#;-#;+0}");
                }
                else
                {
                    sb.AppendLine($"    PSPSymStackSlot: caller.sp{PSPSymStackSlot:+#;-#;+0}");
                }
            }

            if (GenericsInstContextStackSlot != -1)
            {
                sb.AppendLine($"    GenericsInstContextStackSlot: caller.sp{GenericsInstContextStackSlot:+#;-#;+0}");
            }

            if (_machine == Machine.Amd64)
            {
                if (StackBaseRegister != 0xffffffff)
                    sb.AppendLine($"    StackBaseRegister: {(Amd64.Registers)StackBaseRegister}");
                sb.AppendLine($"    Wants Report Only Leaf: {_wantsReportOnlyLeaf}");
            }
            else if (_machine == Machine.ArmThumb2 || _machine == Machine.Arm64)
            {
                if (StackBaseRegister != 0xffffffff)
                {
                    if (_machine == Machine.ArmThumb2)
                    {
                        sb.AppendLine($"    StackBaseRegister: {(Arm.Registers)StackBaseRegister}");
                    }
                    else
                    {
                        sb.AppendLine($"    StackBaseRegister: {(Arm64.Registers)StackBaseRegister}");
                    }
                }

                sb.AppendLine($"    Has Tailcalls: {_wantsReportOnlyLeaf}");
            }
            else if (_machine == Machine.LoongArch64)
            {
                if (StackBaseRegister != 0xffffffff)
                {
                    sb.AppendLine($"    StackBaseRegister: {(LoongArch64.Registers)StackBaseRegister}");
                }
                sb.AppendLine($"    Has Tailcalls: {_wantsReportOnlyLeaf}");
            }
            else if (_machine == Machine.RiscV64)
            {
                if (StackBaseRegister != 0xffffffff)
                {
                    sb.AppendLine($"    StackBaseRegister: {(RiscV64.Registers)StackBaseRegister}");
                }
                sb.AppendLine($"    Has Tailcalls: {_wantsReportOnlyLeaf}");
            }

            sb.AppendLine($"    Size of parameter area: 0x{SizeOfStackOutgoingAndScratchArea:X}");
            if (SizeOfEditAndContinuePreservedArea != 0xffffffff)
                sb.AppendLine($"    SizeOfEditAndContinuePreservedArea: 0x{SizeOfEditAndContinuePreservedArea:X}");
            if (ReversePInvokeFrameStackSlot != -1)
                sb.AppendLine($"    ReversePInvokeFrameStackSlot: {ReversePInvokeFrameStackSlot}");
            sb.AppendLine($"    NumSafePoints: {NumSafePoints}");
            sb.AppendLine($"    NumInterruptibleRanges: {NumInterruptibleRanges}");
            sb.AppendLine($"    SafePointOffsets:");
            foreach (SafePointOffset offset in SafePointOffsets)
            {
                IEnumerable<BaseGcSlot> liveSlotsForOffset = (LiveSlotsAtSafepoints != null ? LiveSlotsAtSafepoints[offset.Index] : Enumerable.Empty<BaseGcSlot>());
                sb.Append($"        0x{offset.Value:X4}: ");
                bool haveLiveSlots = false;
                GcSlotFlags slotFlags = GcSlotFlags.GC_SLOT_INVALID;
                foreach (BaseGcSlot slot in liveSlotsForOffset)
                {
                    if (haveLiveSlots)
                    {
                        sb.Append("; ");
                    }
                    else
                    {
                        haveLiveSlots = true;
                    }
                    slotFlags = slot.WriteTo(sb, _machine, slotFlags);
                }
                if (!haveLiveSlots)
                {
                    sb.Append("no live slots");
                }
                sb.AppendLine();
            }
            sb.AppendLine($"    InterruptibleRanges:");
            foreach (InterruptibleRange range in InterruptibleRanges)
            {
                sb.AppendLine($"        start:{range.StartOffset}, end:{range.StopOffset}");
            }
            sb.AppendLine($"    SlotTable:");
            sb.Append(SlotTable.ToString());
            sb.AppendLine($"    Size: {Size} bytes");

            return sb.ToString();
        }

        /// <summary>
        /// based on <a href="https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/gcinfodecoder.cpp">GcInfoDecoder::GcInfoDecoder</a>
        /// </summary>
        private void ParseHeaderFlags(NativeReader imageReader, ref int bitOffset)
        {
            GcInfoHeaderFlags headerFlags;
            _slimHeader = (imageReader.ReadBits(1, ref bitOffset) == 0);
            if (_slimHeader)
            {
                headerFlags = imageReader.ReadBits(1, ref bitOffset) == 1 ? GcInfoHeaderFlags.GC_INFO_HAS_STACK_BASE_REGISTER : 0;
            }
            else
            {
                int numFlagBits = (int)((Version == 1) ? GcInfoHeaderFlags.GC_INFO_FLAGS_BIT_SIZE_VERSION_1 : GcInfoHeaderFlags.GC_INFO_FLAGS_BIT_SIZE);
                headerFlags = (GcInfoHeaderFlags)imageReader.ReadBits(numFlagBits, ref bitOffset);
            }

            _hasSecurityObject = (headerFlags & GcInfoHeaderFlags.GC_INFO_HAS_SECURITY_OBJECT) != 0;
            _hasGSCookie = (headerFlags & GcInfoHeaderFlags.GC_INFO_HAS_GS_COOKIE) != 0;
            _hasPSPSym = (headerFlags & GcInfoHeaderFlags.GC_INFO_HAS_PSP_SYM) != 0;
            _hasGenericsInstContext = (headerFlags & GcInfoHeaderFlags.GC_INFO_HAS_GENERICS_INST_CONTEXT_MASK) != GcInfoHeaderFlags.GC_INFO_HAS_GENERICS_INST_CONTEXT_NONE;
            _hasStackBaseRegister = (headerFlags & GcInfoHeaderFlags.GC_INFO_HAS_STACK_BASE_REGISTER) != 0;
            _hasSizeOfEditAndContinuePreservedArea = (headerFlags & GcInfoHeaderFlags.GC_INFO_HAS_EDIT_AND_CONTINUE_PRESERVED_SLOTS) != 0;
            if (Version >= MIN_GCINFO_VERSION_WITH_REV_PINVOKE_FRAME) // IsReversePInvokeFrameAvailable
            {
                _hasReversePInvokeFrame = (headerFlags & GcInfoHeaderFlags.GC_INFO_REVERSE_PINVOKE_FRAME) != 0;
            }
            _wantsReportOnlyLeaf = ((headerFlags & GcInfoHeaderFlags.GC_INFO_WANTS_REPORT_ONLY_LEAF) != 0);
        }

        private List<SafePointOffset> EnumerateSafePoints(NativeReader imageReader, ref int bitOffset)
        {
            List<SafePointOffset> safePoints = new List<SafePointOffset>();
            uint numBitsPerOffset = GcInfoTypes.CeilOfLog2((int)_gcInfoTypes.NormalizeCodeOffset((uint)CodeLength));
            for (int i = 0; i < NumSafePoints; i++)
            {
                uint normOffset = (uint)imageReader.ReadBits((int)numBitsPerOffset, ref bitOffset);
                safePoints.Add(new SafePointOffset(i, _gcInfoTypes.DenormalizeCodeOffset(normOffset)));
            }
            return safePoints;
        }

        /// <summary>
        /// based on beginning of <a href="https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/gcinfodecoder.cpp">GcInfoDecoder::EnumerateLiveSlots</a>
        /// </summary>
        private List<InterruptibleRange> EnumerateInterruptibleRanges(NativeReader imageReader, int interruptibleRangeDelta1EncBase, int interruptibleRangeDelta2EncBase, ref int bitOffset)
        {
            List<InterruptibleRange> ranges = new List<InterruptibleRange>();
            uint normLastinterruptibleRangeStopOffset = 0;

            for (uint i = 0; i < NumInterruptibleRanges; i++)
            {
                uint normStartDelta = imageReader.DecodeVarLengthUnsigned(interruptibleRangeDelta1EncBase, ref bitOffset);
                uint normStopDelta = imageReader.DecodeVarLengthUnsigned(interruptibleRangeDelta2EncBase, ref bitOffset) + 1;

                uint normRangeStartOffset = normLastinterruptibleRangeStopOffset + normStartDelta;
                uint normRangeStopOffset = normRangeStartOffset + normStopDelta;

                uint rangeStartOffset = _gcInfoTypes.DenormalizeCodeOffset(normRangeStartOffset);
                uint rangeStopOffset = _gcInfoTypes.DenormalizeCodeOffset(normRangeStopOffset);
                ranges.Add(new InterruptibleRange(i, rangeStartOffset, rangeStopOffset));

                normLastinterruptibleRangeStopOffset = normRangeStopOffset;
            }
            return ranges;
        }

        /// <summary>
        /// GcInfo version is 1 up to ReadyTorun version 1.x.
        /// GcInfo version is current from  ReadyToRun version 2.0
        /// </summary>
        private int ReadyToRunVersionToGcInfoVersion(int readyToRunMajorVersion, int readyToRunMinorVersion)
        {
            if (readyToRunMajorVersion == 1)
                return 1;

            // R2R 2.0+ uses GCInfo v2
            // R2R 9.2+ uses GCInfo v3
            if (readyToRunMajorVersion < 9 || (readyToRunMajorVersion == 9 && readyToRunMinorVersion < 2))
                return 2;

            // R2R 11.0+ uses GCInfo v4
            if (readyToRunMajorVersion < 11)
                return 3;

            return 4;
        }

        private List<List<BaseGcSlot>> GetLiveSlotsAtSafepoints(NativeReader imageReader, ref int bitOffset)
        {
            // For each safe point, enumerates a list of GC slots that are alive at that point
            var result = new List<List<BaseGcSlot>>();

            uint numSlots = SlotTable.NumTracked;
            if (numSlots == 0)
                return null;

            uint numBitsPerOffset = 0;
            // Duplicate the encoder's heuristic to determine if we have indirect live
            // slot table (similar to the chunk pointers)
            if (imageReader.ReadBits(1, ref bitOffset) != 0)
            {
                numBitsPerOffset = imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.POINTER_SIZE_ENCBASE, ref bitOffset) + 1;
                Debug.Assert(numBitsPerOffset != 0);
            }

            uint offsetTablePos = (uint)bitOffset;

            for (uint safePointIndex = 0; safePointIndex < NumSafePoints; safePointIndex++)
            {
                bitOffset = (int)offsetTablePos;

                var liveSlots = new List<BaseGcSlot>();

                if (numBitsPerOffset != 0)
                {
                    bitOffset += (int)(numBitsPerOffset * safePointIndex);

                    uint liveStatesOffset = (uint)imageReader.ReadBits((int)numBitsPerOffset, ref bitOffset);
                    uint liveStatesStart = (uint)((offsetTablePos + NumSafePoints * numBitsPerOffset + 7) & (~7));
                    bitOffset = (int)(liveStatesStart + liveStatesOffset);
                    if (imageReader.ReadBits(1, ref bitOffset) != 0)
                    {
                        // RLE encoded
                        bool skip = imageReader.ReadBits(1, ref bitOffset) == 0;
                        bool report = true;
                        uint readSlots = imageReader.DecodeVarLengthUnsigned(
                            skip ? _gcInfoTypes.LIVESTATE_RLE_SKIP_ENCBASE : _gcInfoTypes.LIVESTATE_RLE_RUN_ENCBASE, ref bitOffset);
                        skip = !skip;
                        while (readSlots < numSlots)
                        {
                            uint cnt = imageReader.DecodeVarLengthUnsigned(
                                skip ? _gcInfoTypes.LIVESTATE_RLE_SKIP_ENCBASE : _gcInfoTypes.LIVESTATE_RLE_RUN_ENCBASE, ref bitOffset) + 1;
                            if (report)
                            {
                                for (uint slotIndex = readSlots; slotIndex < readSlots + cnt; slotIndex++)
                                {
                                    int trackedSlotIndex = 0;
                                    foreach (var slot in SlotTable.GcSlots)
                                    {
                                        if (slot.Flags != GcSlotFlags.GC_SLOT_UNTRACKED)
                                        {
                                            if (slotIndex == trackedSlotIndex)
                                            {
                                                liveSlots.Add(slot);
                                                break;
                                            }
                                            trackedSlotIndex++;
                                        }
                                    }
                                }
                            }
                            readSlots += cnt;
                            skip = !skip;
                            report = !report;
                        }
                        Debug.Assert(readSlots == numSlots);
                        result.Add(liveSlots);
                        continue;
                    }
                    // Just a normal live state (1 bit per slot), so use the normal decoding loop
                }
                else
                {
                    bitOffset += (int)(safePointIndex * numSlots);
                }

                for (uint slotIndex = 0; slotIndex < numSlots; slotIndex++)
                {
                    bool isLive = imageReader.ReadBits(1, ref bitOffset) != 0;
                    if (isLive)
                    {
                        int trackedSlotIndex = 0;
                        foreach (var slot in SlotTable.GcSlots)
                        {
                            if (slot.Flags != GcSlotFlags.GC_SLOT_UNTRACKED)
                            {
                                if (slotIndex == trackedSlotIndex)
                                {
                                    liveSlots.Add(slot);
                                    break;
                                }
                                trackedSlotIndex++;
                            }
                        }
                    }
                }

                result.Add(liveSlots);
            }

            return result;
        }

        /// <summary>
        /// based on end of <a href="https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/gcinfodecoder.cpp">GcInfoDecoder::EnumerateLiveSlots and GcInfoEncoder::Build</a>
        /// </summary>
        private Dictionary<int, List<BaseGcTransition>> GetTransitions(NativeReader imageReader, ref int bitOffset)
        {
            int totalInterruptibleLength = 0;
            if (NumInterruptibleRanges == 0)
            {
                totalInterruptibleLength = _gcInfoTypes.NormalizeCodeLength(CodeLength);
            }
            else
            {
                foreach (InterruptibleRange range in InterruptibleRanges)
                {
                    uint normStart = _gcInfoTypes.NormalizeCodeOffset(range.StartOffset);
                    uint normStop = _gcInfoTypes.NormalizeCodeOffset(range.StopOffset);
                    totalInterruptibleLength += (int)(normStop - normStart);
                }
            }

            if (SlotTable.NumTracked == 0)
                return new Dictionary<int, List<BaseGcTransition>>();

            int numChunks = (totalInterruptibleLength + _gcInfoTypes.NUM_NORM_CODE_OFFSETS_PER_CHUNK - 1) / _gcInfoTypes.NUM_NORM_CODE_OFFSETS_PER_CHUNK;
            int numBitsPerPointer = (int)imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.POINTER_SIZE_ENCBASE, ref bitOffset);
            if (numBitsPerPointer == 0)
            {
                return new Dictionary<int, List<BaseGcTransition>>();
            }

            // get offsets of each chunk
            int[] chunkPointers = new int[numChunks];
            for (int i = 0; i < numChunks; i++)
            {
                chunkPointers[i] = imageReader.ReadBits(numBitsPerPointer, ref bitOffset);
            }

            // Offset to m_Info2 containing all the info on register liveness, which starts at the next byte
            int info2Offset = (bitOffset + 7) & ~7;

            List<GcTransition> transitions = new List<GcTransition>();
            bool[] liveAtEnd = new bool[SlotTable.NumTracked]; // true if slot is live at the end of the chunk
            for (int currentChunk = 0; currentChunk < numChunks; currentChunk++)
            {
                if (chunkPointers[currentChunk] == 0)
                {
                    continue;
                }
                else
                {
                    bitOffset = info2Offset + chunkPointers[currentChunk] - 1;
                }

                int couldBeLiveOffset = bitOffset; // points to the couldBeLive bit array (array of bits indicating the slot changed state in the chunk)
                int slotId = 0;
                bool fSimple = (imageReader.ReadBits(1, ref couldBeLiveOffset) == 0);
                bool fSkipFirst = false;
                int couldBeLiveCnt = 0;
                if (!fSimple)
                {
                    fSkipFirst = (imageReader.ReadBits(1, ref couldBeLiveOffset) == 0);
                    slotId = -1;
                }

                uint numCouldBeLiveSlots = GetNumCouldBeLiveSlots(imageReader, ref bitOffset); // count the number of set bits in the couldBeLive array

                int finalStateOffset = bitOffset; // points to the finalState bit array (array of bits indicating if the slot is live at the end of the chunk)
                bitOffset += (int)numCouldBeLiveSlots; // points to the array of code offsets

                int normChunkBaseCodeOffset = currentChunk * _gcInfoTypes.NUM_NORM_CODE_OFFSETS_PER_CHUNK; // the sum of the sizes of all preceding chunks
                for (int i = 0; i < numCouldBeLiveSlots; i++)
                {
                    // get the index of the next couldBeLive slot
                    slotId = GetNextSlotId(imageReader, fSimple, fSkipFirst, slotId, ref couldBeLiveCnt, ref couldBeLiveOffset);

                    // set the liveAtEnd for the slot at slotId
                    bool isLive = !liveAtEnd[slotId];
                    liveAtEnd[slotId] = (imageReader.ReadBits(1, ref finalStateOffset) != 0);

                    // Read all the code offsets where the slot at slotId changed state
                    while (imageReader.ReadBits(1, ref bitOffset) != 0)
                    {
                        int transitionOffset = imageReader.ReadBits(_gcInfoTypes.NUM_NORM_CODE_OFFSETS_PER_CHUNK_LOG2, ref bitOffset) + normChunkBaseCodeOffset;
                        transitions.Add(new GcTransition(transitionOffset, slotId, isLive, currentChunk, SlotTable, _machine));
                        isLive = !isLive;
                    }
                    slotId++;
                }
            }

            // convert normCodeOffsetDelta to the actual CodeOffset
            transitions.Sort((s1, s2) => s1.CodeOffset.CompareTo(s2.CodeOffset));
            return UpdateTransitionCodeOffset(transitions);
        }

        private uint GetNumCouldBeLiveSlots(NativeReader imageReader, ref int bitOffset)
        {
            uint numCouldBeLiveSlots = 0;
            uint numTracked = SlotTable.NumTracked;
            if (imageReader.ReadBits(1, ref bitOffset) != 0)
            {
                // RLE encoded
                bool fSkip = (imageReader.ReadBits(1, ref bitOffset) == 0);
                bool fReport = true;
                uint readSlots = imageReader.DecodeVarLengthUnsigned(fSkip ? _gcInfoTypes.LIVESTATE_RLE_SKIP_ENCBASE : _gcInfoTypes.LIVESTATE_RLE_RUN_ENCBASE, ref bitOffset);
                fSkip = !fSkip;
                while (readSlots < numTracked)
                {
                    uint cnt = imageReader.DecodeVarLengthUnsigned(fSkip ? _gcInfoTypes.LIVESTATE_RLE_SKIP_ENCBASE : _gcInfoTypes.LIVESTATE_RLE_RUN_ENCBASE, ref bitOffset) + 1;
                    if (fReport)
                    {
                        numCouldBeLiveSlots += cnt;
                    }
                    readSlots += cnt;
                    fSkip = !fSkip;
                    fReport = !fReport;
                }
                Debug.Assert(readSlots == numTracked);
            }
            else
            {
                // count the number of set bits in the couldBeLive bit array
                foreach (var slot in SlotTable.GcSlots)
                {
                    if ((slot.Flags & GcSlotFlags.GC_SLOT_UNTRACKED) != 0)
                        break;

                    if (imageReader.ReadBits(1, ref bitOffset) != 0)
                        numCouldBeLiveSlots++;
                }
            }
            Debug.Assert(numCouldBeLiveSlots > 0);
            return numCouldBeLiveSlots;
        }

        private int GetNextSlotId(NativeReader imageReader, bool fSimple, bool fSkipFirst, int slotId, ref int couldBeLiveCnt, ref int couldBeLiveOffset)
        {
            if (fSimple)
            {
                // Get the slotId by iterating through the couldBeLive bit array. The slotId is the index of the next set bit
                while (imageReader.ReadBits(1, ref couldBeLiveOffset) == 0)
                    slotId++;
            }
            else if (couldBeLiveCnt > 0)
            {
                // We have more from the last run to report
                couldBeLiveCnt--;
            }
            // We need to find a new run
            else if (fSkipFirst)
            {
                int tmp = (int)imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.LIVESTATE_RLE_SKIP_ENCBASE, ref couldBeLiveOffset) + 1;
                slotId += tmp;
                couldBeLiveCnt = (int)imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.LIVESTATE_RLE_RUN_ENCBASE, ref couldBeLiveOffset);
            }
            else
            {
                int tmp = (int)imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.LIVESTATE_RLE_RUN_ENCBASE, ref couldBeLiveOffset) + 1;
                slotId += tmp;
                couldBeLiveCnt = (int)imageReader.DecodeVarLengthUnsigned(_gcInfoTypes.LIVESTATE_RLE_SKIP_ENCBASE, ref couldBeLiveOffset);
            }
            return slotId;
        }

        /// <summary>
        /// convert normCodeOffsetDelta to the actual CodeOffset
        /// </summary>
        private Dictionary<int, List<BaseGcTransition>> UpdateTransitionCodeOffset(List<GcTransition> transitions)
        {
            Dictionary<int, List<BaseGcTransition>> updatedTransitions = new Dictionary<int, List<BaseGcTransition>>();
            int cumInterruptibleLength = 0; // the sum of the lengths of all preceding interruptible ranges
            using (IEnumerator<InterruptibleRange> interruptibleRangesIter = InterruptibleRanges.GetEnumerator())
            {
                interruptibleRangesIter.MoveNext();
                InterruptibleRange currentRange = interruptibleRangesIter.Current;
                int currentRangeLength = (int)(currentRange.StopOffset - currentRange.StartOffset);
                foreach (GcTransition transition in transitions)
                {
                    int codeOffset = transition.CodeOffset + (int)currentRange.StartOffset - cumInterruptibleLength;
                    if (codeOffset > currentRange.StopOffset)
                    {
                        cumInterruptibleLength += currentRangeLength;
                        interruptibleRangesIter.MoveNext();
                        currentRange = interruptibleRangesIter.Current;
                        currentRangeLength = (int)(currentRange.StopOffset - currentRange.StartOffset);
                        codeOffset = transition.CodeOffset + (int)currentRange.StartOffset - cumInterruptibleLength;
                    }

                    transition.CodeOffset = codeOffset;
                    if (!updatedTransitions.ContainsKey(codeOffset))
                    {
                        updatedTransitions[codeOffset] = new List<BaseGcTransition>();
                    }
                    updatedTransitions[codeOffset].Add(transition);
                }
            }
            return updatedTransitions;
        }
    }
}
