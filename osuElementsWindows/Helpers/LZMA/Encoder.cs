// LzmaEncoder.cs

using System;
using System.IO;

namespace osuElements.Helpers.LZMA
{
    internal class Encoder
    {
        private enum EMatchFinderType
        {
            BT4=1,
        }

        private const uint kIfinityPrice = 0xFFFFFFF;

        private static readonly byte[] g_FastPos = new byte[1 << 11];

        static Encoder() {
            const byte kFastSlots = 22;
            var c = 2;
            g_FastPos[0] = 0;
            g_FastPos[1] = 1;
            for (byte slotFast = 2; slotFast < kFastSlots; slotFast++) {
                var k = (uint)1 << ((slotFast >> 1) - 1);
                for (uint j = 0; j < k; j++, c++)
                    g_FastPos[c] = slotFast;
            }
        }

        private static uint GetPosSlot(uint pos) {
            if (pos < 1 << 11)
                return g_FastPos[pos];
            if (pos < 1 << 21)
                return (uint)(g_FastPos[pos >> 10] + 20);
            return (uint)(g_FastPos[pos >> 20] + 40);
        }

        private static uint GetPosSlot2(uint pos) {
            if (pos < 1 << 17)
                return (uint)(g_FastPos[pos >> 6] + 12);
            if (pos < 1 << 27)
                return (uint)(g_FastPos[pos >> 16] + 32);
            return (uint)(g_FastPos[pos >> 26] + 52);
        }

        private Base.State _state = new Base.State();
        private byte _previousByte;
        private readonly uint[] _repDistances = new uint[Base.kNumRepDistances];

        private const int kDefaultDictionaryLogSize = 22;
        private const uint kNumFastBytesDefault = 0x20;

        private class LiteralEncoder
        {
            public struct Encoder2
            {
                private BitEncoder[] m_Encoders;

                public void Encode(RangeCoder rangeRangeCoder, byte symbol) {
                    uint context = 1;
                    for (var i = 7; i >= 0; i--) {
                        var bit = (uint)((symbol >> i) & 1);
                        m_Encoders[context].Encode(rangeRangeCoder, bit);
                        context = (context << 1) | bit;
                    }
                }

                public void EncodeMatched(RangeCoder rangeRangeCoder, byte matchByte, byte symbol) {
                    uint context = 1;
                    var same = true;
                    for (var i = 7; i >= 0; i--) {
                        var bit = (uint)((symbol >> i) & 1);
                        var state = context;
                        if (same) {
                            var matchBit = (uint)((matchByte >> i) & 1);
                            state += (1 + matchBit) << 8;
                            same = matchBit == bit;
                        }
                        m_Encoders[state].Encode(rangeRangeCoder, bit);
                        context = (context << 1) | bit;
                    }
                }

                public uint GetPrice(bool matchMode, byte matchByte, byte symbol) {
                    uint price = 0;
                    uint context = 1;
                    var i = 7;
                    if (matchMode) {
                        for (; i >= 0; i--) {
                            var matchBit = (uint)(matchByte >> i) & 1;
                            var bit = (uint)(symbol >> i) & 1;
                            price += m_Encoders[((1 + matchBit) << 8) + context].GetPrice(bit);
                            context = (context << 1) | bit;
                            if (matchBit != bit) {
                                i--;
                                break;
                            }
                        }
                    }
                    for (; i >= 0; i--) {
                        var bit = (uint)(symbol >> i) & 1;
                        price += m_Encoders[context].GetPrice(bit);
                        context = (context << 1) | bit;
                    }
                    return price;
                }
            }

            private Encoder2[] m_Coders;
            private int m_NumPrevBits;
            private int m_NumPosBits;
            private uint m_PosMask;


            public Encoder2 GetSubCoder(uint pos, byte prevByte) { return m_Coders[((pos & m_PosMask) << m_NumPrevBits) + (uint)(prevByte >> (8 - m_NumPrevBits))]; }
        }

        private class LenEncoder
        {
            private BitEncoder _choice = new BitEncoder();
            private BitEncoder _choice2 = new BitEncoder();
            private readonly BitTreeEncoder[] _lowCoder = new BitTreeEncoder[Base.kNumPosStatesEncodingMax];
            private readonly BitTreeEncoder[] _midCoder = new BitTreeEncoder[Base.kNumPosStatesEncodingMax];
            private BitTreeEncoder _highCoder = new BitTreeEncoder(Base.kNumHighLenBits);


            protected void Encode(RangeCoder rangeRangeCoder, uint symbol, uint posState) {
                if (symbol < Base.kNumLowLenSymbols) {
                    _choice.Encode(rangeRangeCoder, 0);
                    _lowCoder[posState].Encode(rangeRangeCoder, symbol);
                }
                else {
                    symbol -= Base.kNumLowLenSymbols;
                    _choice.Encode(rangeRangeCoder, 1);
                    if (symbol < Base.kNumMidLenSymbols) {
                        _choice2.Encode(rangeRangeCoder, 0);
                        _midCoder[posState].Encode(rangeRangeCoder, symbol);
                    }
                    else {
                        _choice2.Encode(rangeRangeCoder, 1);
                        _highCoder.Encode(rangeRangeCoder, symbol - Base.kNumMidLenSymbols);
                    }
                }
            }

            protected void SetPrices(uint posState, uint numSymbols, uint[] prices, uint st) {
                var a0 = _choice.GetPrice0();
                var a1 = _choice.GetPrice1();
                var b0 = a1 + _choice2.GetPrice0();
                var b1 = a1 + _choice2.GetPrice1();
                uint i;
                for (i = 0; i < Base.kNumLowLenSymbols; i++) {
                    if (i >= numSymbols)
                        return;
                    prices[st + i] = a0 + _lowCoder[posState].GetPrice(i);
                }
                for (; i < Base.kNumLowLenSymbols + Base.kNumMidLenSymbols; i++) {
                    if (i >= numSymbols)
                        return;
                    prices[st + i] = b0 + _midCoder[posState].GetPrice(i - Base.kNumLowLenSymbols);
                }
                for (; i < numSymbols; i++)
                    prices[st + i] = b1 + _highCoder.GetPrice(i - Base.kNumLowLenSymbols - Base.kNumMidLenSymbols);
            }
        }


        private class LenPriceTableEncoder : LenEncoder
        {
            private readonly uint[] _prices = new uint[Base.kNumLenSymbols << Base.kNumPosStatesBitsEncodingMax];
            private uint _tableSize;
            private readonly uint[] _counters = new uint[Base.kNumPosStatesEncodingMax];


            public uint GetPrice(uint symbol, uint posState) {
                return _prices[posState * Base.kNumLenSymbols + symbol];
            }

            private void UpdateTable(uint posState) {
                SetPrices(posState, _tableSize, _prices, posState * Base.kNumLenSymbols);
                _counters[posState] = _tableSize;
            }


            public new void Encode(RangeCoder rangeRangeCoder, uint symbol, uint posState) {
                base.Encode(rangeRangeCoder, symbol, posState);
                if (--_counters[posState] == 0)
                    UpdateTable(posState);
            }
        }

        private const uint kNumOpts = 1 << 12;

        private class Optimal
        {
            public Base.State State;

            public bool Prev1IsChar;
            public bool Prev2;

            public uint PosPrev2;
            public uint BackPrev2;

            public uint Price;
            public uint PosPrev;
            public uint BackPrev;

            public uint Backs0;
            public uint Backs1;
            public uint Backs2;
            public uint Backs3;

            public void MakeAsChar() { BackPrev = 0xFFFFFFFF; Prev1IsChar = false; }
            public void MakeAsShortRep() { BackPrev = 0; ; Prev1IsChar = false; }
            public bool IsShortRep() => BackPrev == 0;
        }

        private readonly Optimal[] _optimum = new Optimal[kNumOpts];
        private IMatchFinder _matchFinder;
        private readonly RangeCoder _rangeRangeCoder = new RangeCoder();

        private readonly BitEncoder[] _isMatch = new BitEncoder[Base.kNumStates << Base.kNumPosStatesBitsMax];
        private readonly BitEncoder[] _isRep = new BitEncoder[Base.kNumStates];
        private readonly BitEncoder[] _isRepG0 = new BitEncoder[Base.kNumStates];
        private readonly BitEncoder[] _isRepG1 = new BitEncoder[Base.kNumStates];
        private readonly BitEncoder[] _isRepG2 = new BitEncoder[Base.kNumStates];
        private readonly BitEncoder[] _isRep0Long = new BitEncoder[Base.kNumStates << Base.kNumPosStatesBitsMax];

        private readonly BitTreeEncoder[] _posSlotEncoder = new BitTreeEncoder[Base.kNumLenToPosStates];

        private readonly BitEncoder[] _posEncoders = new BitEncoder[Base.kNumFullDistances - Base.kEndPosModelIndex];
        private BitTreeEncoder _posAlignEncoder = new BitTreeEncoder(Base.kNumAlignBits);

        private readonly LenPriceTableEncoder _lenEncoder = new LenPriceTableEncoder();
        private readonly LenPriceTableEncoder _repMatchLenEncoder = new LenPriceTableEncoder();

        private readonly LiteralEncoder _literalEncoder = new LiteralEncoder();

        private readonly uint[] _matchDistances = new uint[Base.kMatchMaxLen * 2 + 2];

        private uint _numFastBytes = kNumFastBytesDefault;
        private uint _longestMatchLength;
        private uint _numDistancePairs;

        private uint _additionalOffset;

        private uint _optimumEndIndex;
        private uint _optimumCurrentIndex;

        private bool _longestMatchWasFound;

        private readonly uint[] _posSlotPrices = new uint[1 << (Base.kNumPosSlotBits + Base.kNumLenToPosStatesBits)];
        private readonly uint[] _distancesPrices = new uint[Base.kNumFullDistances << Base.kNumLenToPosStatesBits];
        private readonly uint[] _alignPrices = new uint[Base.kAlignTableSize];
        private uint _alignPriceCount;

        private uint _distTableSize = kDefaultDictionaryLogSize * 2;

        private int _posStateBits = 2;
        private uint _posStateMask = 4 - 1;
        private int _numLiteralPosStateBits;
        private int _numLiteralContextBits = 3;

        private uint _dictionarySize = 1 << kDefaultDictionaryLogSize;
        private uint _dictionarySizePrev = 0xFFFFFFFF;
        private uint _numFastBytesPrev = 0xFFFFFFFF;

        private long nowPos64;
        private bool _finished;
        private Stream _inStream;

        private EMatchFinderType _matchFinderType = EMatchFinderType.BT4;
        private bool _writeEndMark;

        private bool _needReleaseMFStream;


        public Encoder() {
            for (var i = 0; i < kNumOpts; i++)
                _optimum[i] = new Optimal();
            for (var i = 0; i < Base.kNumLenToPosStates; i++)
                _posSlotEncoder[i] = new BitTreeEncoder(Base.kNumPosSlotBits);
        }

        private void SetWriteEndMarkerMode(bool writeEndMarker) {
            _writeEndMark = writeEndMarker;
        }


        private void ReadMatchDistances(out uint lenRes, out uint numDistancePairs) {
            lenRes = 0;
            numDistancePairs = _matchFinder.GetMatches(_matchDistances);
            if (numDistancePairs > 0) {
                lenRes = _matchDistances[numDistancePairs - 2];
                if (lenRes == _numFastBytes)
                    lenRes += _matchFinder.GetMatchLen((int)lenRes - 1, _matchDistances[numDistancePairs - 1],
                        Base.kMatchMaxLen - lenRes);
            }
            _additionalOffset++;
        }


        private void MovePos(uint num) {
            if (num > 0) {
                _matchFinder.Skip(num);
                _additionalOffset += num;
            }
        }

        private uint GetRepLen1Price(Base.State state, uint posState) {
            return _isRepG0[state.Index].GetPrice0() +
                    _isRep0Long[(state.Index << Base.kNumPosStatesBitsMax) + posState].GetPrice0();
        }

        private uint GetPureRepPrice(uint repIndex, Base.State state, uint posState) {
            uint price;
            if (repIndex == 0) {
                price = _isRepG0[state.Index].GetPrice0();
                price += _isRep0Long[(state.Index << Base.kNumPosStatesBitsMax) + posState].GetPrice1();
            }
            else {
                price = _isRepG0[state.Index].GetPrice1();
                if (repIndex == 1)
                    price += _isRepG1[state.Index].GetPrice0();
                else {
                    price += _isRepG1[state.Index].GetPrice1();
                    price += _isRepG2[state.Index].GetPrice(repIndex - 2);
                }
            }
            return price;
        }

        private uint GetRepPrice(uint repIndex, uint len, Base.State state, uint posState) {
            var price = _repMatchLenEncoder.GetPrice(len - Base.kMatchMinLen, posState);
            return price + GetPureRepPrice(repIndex, state, posState);
        }

        private uint GetPosLenPrice(uint pos, uint len, uint posState) {
            uint price;
            var lenToPosState = Base.GetLenToPosState(len);
            if (pos < Base.kNumFullDistances)
                price = _distancesPrices[lenToPosState * Base.kNumFullDistances + pos];
            else
                price = _posSlotPrices[(lenToPosState << Base.kNumPosSlotBits) + GetPosSlot2(pos)] +
                    _alignPrices[pos & Base.kAlignMask];
            return price + _lenEncoder.GetPrice(len - Base.kMatchMinLen, posState);
        }

        private uint Backward(out uint backRes, uint cur) {
            _optimumEndIndex = cur;
            var posMem = _optimum[cur].PosPrev;
            var backMem = _optimum[cur].BackPrev;
            do {
                if (_optimum[cur].Prev1IsChar) {
                    _optimum[posMem].MakeAsChar();
                    _optimum[posMem].PosPrev = posMem - 1;
                    if (_optimum[cur].Prev2) {
                        _optimum[posMem - 1].Prev1IsChar = false;
                        _optimum[posMem - 1].PosPrev = _optimum[cur].PosPrev2;
                        _optimum[posMem - 1].BackPrev = _optimum[cur].BackPrev2;
                    }
                }
                var posPrev = posMem;
                var backCur = backMem;

                backMem = _optimum[posPrev].BackPrev;
                posMem = _optimum[posPrev].PosPrev;

                _optimum[posPrev].BackPrev = backCur;
                _optimum[posPrev].PosPrev = cur;
                cur = posPrev;
            }
            while (cur > 0);
            backRes = _optimum[0].BackPrev;
            _optimumCurrentIndex = _optimum[0].PosPrev;
            return _optimumCurrentIndex;
        }

        private readonly uint[] reps = new uint[Base.kNumRepDistances];
        private readonly uint[] repLens = new uint[Base.kNumRepDistances];


        private uint GetOptimum(uint position, out uint backRes) {
            if (_optimumEndIndex != _optimumCurrentIndex) {
                var lenRes = _optimum[_optimumCurrentIndex].PosPrev - _optimumCurrentIndex;
                backRes = _optimum[_optimumCurrentIndex].BackPrev;
                _optimumCurrentIndex = _optimum[_optimumCurrentIndex].PosPrev;
                return lenRes;
            }
            _optimumCurrentIndex = _optimumEndIndex = 0;

            uint lenMain, numDistancePairs;
            if (!_longestMatchWasFound) {
                ReadMatchDistances(out lenMain, out numDistancePairs);
            }
            else {
                lenMain = _longestMatchLength;
                numDistancePairs = _numDistancePairs;
                _longestMatchWasFound = false;
            }

            var numAvailableBytes = _matchFinder.GetNumAvailableBytes() + 1;
            if (numAvailableBytes < 2) {
                backRes = 0xFFFFFFFF;
                return 1;
            }

            uint repMaxIndex = 0;
            uint i;
            for (i = 0; i < Base.kNumRepDistances; i++) {
                reps[i] = _repDistances[i];
                repLens[i] = _matchFinder.GetMatchLen(0 - 1, reps[i], Base.kMatchMaxLen);
                if (repLens[i] > repLens[repMaxIndex])
                    repMaxIndex = i;
            }
            if (repLens[repMaxIndex] >= _numFastBytes) {
                backRes = repMaxIndex;
                var lenRes = repLens[repMaxIndex];
                MovePos(lenRes - 1);
                return lenRes;
            }

            if (lenMain >= _numFastBytes) {
                backRes = _matchDistances[numDistancePairs - 1] + Base.kNumRepDistances;
                MovePos(lenMain - 1);
                return lenMain;
            }

            var currentByte = _matchFinder.GetIndexByte(0 - 1);
            var matchByte = _matchFinder.GetIndexByte((int)(0 - _repDistances[0] - 1 - 1));

            if (lenMain < 2 && currentByte != matchByte && repLens[repMaxIndex] < 2) {
                backRes = 0xFFFFFFFF;
                return 1;
            }

            _optimum[0].State = _state;

            var posState = position & _posStateMask;

            _optimum[1].Price = _isMatch[(_state.Index << Base.kNumPosStatesBitsMax) + posState].GetPrice0() +
                    _literalEncoder.GetSubCoder(position, _previousByte).GetPrice(!_state.IsCharState(), matchByte, currentByte);
            _optimum[1].MakeAsChar();

            var matchPrice = _isMatch[(_state.Index << Base.kNumPosStatesBitsMax) + posState].GetPrice1();
            var repMatchPrice = matchPrice + _isRep[_state.Index].GetPrice1();

            if (matchByte == currentByte) {
                var shortRepPrice = repMatchPrice + GetRepLen1Price(_state, posState);
                if (shortRepPrice < _optimum[1].Price) {
                    _optimum[1].Price = shortRepPrice;
                    _optimum[1].MakeAsShortRep();
                }
            }

            var lenEnd = lenMain >= repLens[repMaxIndex] ? lenMain : repLens[repMaxIndex];

            if (lenEnd < 2) {
                backRes = _optimum[1].BackPrev;
                return 1;
            }

            _optimum[1].PosPrev = 0;

            _optimum[0].Backs0 = reps[0];
            _optimum[0].Backs1 = reps[1];
            _optimum[0].Backs2 = reps[2];
            _optimum[0].Backs3 = reps[3];

            var len = lenEnd;
            do
                _optimum[len--].Price = kIfinityPrice;
            while (len >= 2);

            for (i = 0; i < Base.kNumRepDistances; i++) {
                var repLen = repLens[i];
                if (repLen < 2)
                    continue;
                var price = repMatchPrice + GetPureRepPrice(i, _state, posState);
                do {
                    var curAndLenPrice = price + _repMatchLenEncoder.GetPrice(repLen - 2, posState);
                    var optimum = _optimum[repLen];
                    if (curAndLenPrice < optimum.Price) {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = 0;
                        optimum.BackPrev = i;
                        optimum.Prev1IsChar = false;
                    }
                }
                while (--repLen >= 2);
            }

            var normalMatchPrice = matchPrice + _isRep[_state.Index].GetPrice0();

            len = repLens[0] >= 2 ? repLens[0] + 1 : 2;
            if (len <= lenMain) {
                uint offs = 0;
                while (len > _matchDistances[offs])
                    offs += 2;
                for (; ; len++) {
                    var distance = _matchDistances[offs + 1];
                    var curAndLenPrice = normalMatchPrice + GetPosLenPrice(distance, len, posState);
                    var optimum = _optimum[len];
                    if (curAndLenPrice < optimum.Price) {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = 0;
                        optimum.BackPrev = distance + Base.kNumRepDistances;
                        optimum.Prev1IsChar = false;
                    }
                    if (len == _matchDistances[offs]) {
                        offs += 2;
                        if (offs == numDistancePairs)
                            break;
                    }
                }
            }

            uint cur = 0;

            while (true) {
                cur++;
                if (cur == lenEnd)
                    return Backward(out backRes, cur);
                uint newLen;
                ReadMatchDistances(out newLen, out numDistancePairs);
                if (newLen >= _numFastBytes) {
                    _numDistancePairs = numDistancePairs;
                    _longestMatchLength = newLen;
                    _longestMatchWasFound = true;
                    return Backward(out backRes, cur);
                }
                position++;
                var posPrev = _optimum[cur].PosPrev;
                Base.State state;
                if (_optimum[cur].Prev1IsChar) {
                    posPrev--;
                    if (_optimum[cur].Prev2) {
                        state = _optimum[_optimum[cur].PosPrev2].State;
                        if (_optimum[cur].BackPrev2 < Base.kNumRepDistances)
                            state.UpdateRep();
                        else
                            state.UpdateMatch();
                    }
                    else
                        state = _optimum[posPrev].State;
                    state.UpdateChar();
                }
                else
                    state = _optimum[posPrev].State;
                if (posPrev == cur - 1) {
                    if (_optimum[cur].IsShortRep())
                        state.UpdateShortRep();
                    else
                        state.UpdateChar();
                }
                else {
                    uint pos;
                    if (_optimum[cur].Prev1IsChar && _optimum[cur].Prev2) {
                        posPrev = _optimum[cur].PosPrev2;
                        pos = _optimum[cur].BackPrev2;
                        state.UpdateRep();
                    }
                    else {
                        pos = _optimum[cur].BackPrev;
                        if (pos < Base.kNumRepDistances)
                            state.UpdateRep();
                        else
                            state.UpdateMatch();
                    }
                    var opt = _optimum[posPrev];
                    if (pos < Base.kNumRepDistances) {
                        if (pos == 0) {
                            reps[0] = opt.Backs0;
                            reps[1] = opt.Backs1;
                            reps[2] = opt.Backs2;
                            reps[3] = opt.Backs3;
                        }
                        else if (pos == 1) {
                            reps[0] = opt.Backs1;
                            reps[1] = opt.Backs0;
                            reps[2] = opt.Backs2;
                            reps[3] = opt.Backs3;
                        }
                        else if (pos == 2) {
                            reps[0] = opt.Backs2;
                            reps[1] = opt.Backs0;
                            reps[2] = opt.Backs1;
                            reps[3] = opt.Backs3;
                        }
                        else {
                            reps[0] = opt.Backs3;
                            reps[1] = opt.Backs0;
                            reps[2] = opt.Backs1;
                            reps[3] = opt.Backs2;
                        }
                    }
                    else {
                        reps[0] = pos - Base.kNumRepDistances;
                        reps[1] = opt.Backs0;
                        reps[2] = opt.Backs1;
                        reps[3] = opt.Backs2;
                    }
                }
                _optimum[cur].State = state;
                _optimum[cur].Backs0 = reps[0];
                _optimum[cur].Backs1 = reps[1];
                _optimum[cur].Backs2 = reps[2];
                _optimum[cur].Backs3 = reps[3];
                var curPrice = _optimum[cur].Price;

                currentByte = _matchFinder.GetIndexByte(0 - 1);
                matchByte = _matchFinder.GetIndexByte((int)(0 - reps[0] - 1 - 1));

                posState = position & _posStateMask;

                var curAnd1Price = curPrice +
                    _isMatch[(state.Index << Base.kNumPosStatesBitsMax) + posState].GetPrice0() +
                    _literalEncoder.GetSubCoder(position, _matchFinder.GetIndexByte(0 - 2)).
                    GetPrice(!state.IsCharState(), matchByte, currentByte);

                var nextOptimum = _optimum[cur + 1];

                var nextIsChar = false;
                if (curAnd1Price < nextOptimum.Price) {
                    nextOptimum.Price = curAnd1Price;
                    nextOptimum.PosPrev = cur;
                    nextOptimum.MakeAsChar();
                    nextIsChar = true;
                }

                matchPrice = curPrice + _isMatch[(state.Index << Base.kNumPosStatesBitsMax) + posState].GetPrice1();
                repMatchPrice = matchPrice + _isRep[state.Index].GetPrice1();

                if (matchByte == currentByte &&
                    !(nextOptimum.PosPrev < cur && nextOptimum.BackPrev == 0)) {
                    var shortRepPrice = repMatchPrice + GetRepLen1Price(state, posState);
                    if (shortRepPrice <= nextOptimum.Price) {
                        nextOptimum.Price = shortRepPrice;
                        nextOptimum.PosPrev = cur;
                        nextOptimum.MakeAsShortRep();
                        nextIsChar = true;
                    }
                }

                var numAvailableBytesFull = _matchFinder.GetNumAvailableBytes() + 1;
                numAvailableBytesFull = Math.Min(kNumOpts - 1 - cur, numAvailableBytesFull);
                numAvailableBytes = numAvailableBytesFull;

                if (numAvailableBytes < 2)
                    continue;
                if (numAvailableBytes > _numFastBytes)
                    numAvailableBytes = _numFastBytes;
                if (!nextIsChar && matchByte != currentByte) {
                    // try Literal + rep0
                    var t = Math.Min(numAvailableBytesFull - 1, _numFastBytes);
                    var lenTest2 = _matchFinder.GetMatchLen(0, reps[0], t);
                    if (lenTest2 >= 2) {
                        var state2 = state;
                        state2.UpdateChar();
                        var posStateNext = (position + 1) & _posStateMask;
                        var nextRepMatchPrice = curAnd1Price +
                            _isMatch[(state2.Index << Base.kNumPosStatesBitsMax) + posStateNext].GetPrice1() +
                            _isRep[state2.Index].GetPrice1();
                        {
                            var offset = cur + 1 + lenTest2;
                            while (lenEnd < offset)
                                _optimum[++lenEnd].Price = kIfinityPrice;
                            var curAndLenPrice = nextRepMatchPrice + GetRepPrice(
                                0, lenTest2, state2, posStateNext);
                            var optimum = _optimum[offset];
                            if (curAndLenPrice < optimum.Price) {
                                optimum.Price = curAndLenPrice;
                                optimum.PosPrev = cur + 1;
                                optimum.BackPrev = 0;
                                optimum.Prev1IsChar = true;
                                optimum.Prev2 = false;
                            }
                        }
                    }
                }

                uint startLen = 2; // speed optimization 

                for (uint repIndex = 0; repIndex < Base.kNumRepDistances; repIndex++) {
                    var lenTest = _matchFinder.GetMatchLen(0 - 1, reps[repIndex], numAvailableBytes);
                    if (lenTest < 2)
                        continue;
                    var lenTestTemp = lenTest;
                    do {
                        while (lenEnd < cur + lenTest)
                            _optimum[++lenEnd].Price = kIfinityPrice;
                        var curAndLenPrice = repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState);
                        var optimum = _optimum[cur + lenTest];
                        if (curAndLenPrice < optimum.Price) {
                            optimum.Price = curAndLenPrice;
                            optimum.PosPrev = cur;
                            optimum.BackPrev = repIndex;
                            optimum.Prev1IsChar = false;
                        }
                    }
                    while (--lenTest >= 2);
                    lenTest = lenTestTemp;

                    if (repIndex == 0)
                        startLen = lenTest + 1;

                    // if (_maxMode)
                    if (lenTest < numAvailableBytesFull) {
                        var t = Math.Min(numAvailableBytesFull - 1 - lenTest, _numFastBytes);
                        var lenTest2 = _matchFinder.GetMatchLen((int)lenTest, reps[repIndex], t);
                        if (lenTest2 >= 2) {
                            var state2 = state;
                            state2.UpdateRep();
                            var posStateNext = (position + lenTest) & _posStateMask;
                            var curAndLenCharPrice =
                                    repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState) +
                                    _isMatch[(state2.Index << Base.kNumPosStatesBitsMax) + posStateNext].GetPrice0() +
                                    _literalEncoder.GetSubCoder(position + lenTest,
                                    _matchFinder.GetIndexByte((int)lenTest - 1 - 1)).GetPrice(true,
                                    _matchFinder.GetIndexByte((int)lenTest - 1 - (int)(reps[repIndex] + 1)),
                                    _matchFinder.GetIndexByte((int)lenTest - 1));
                            state2.UpdateChar();
                            posStateNext = (position + lenTest + 1) & _posStateMask;
                            var nextMatchPrice = curAndLenCharPrice + _isMatch[(state2.Index << Base.kNumPosStatesBitsMax) + posStateNext].GetPrice1();
                            var nextRepMatchPrice = nextMatchPrice + _isRep[state2.Index].GetPrice1();

                            // for(; lenTest2 >= 2; lenTest2--)
                            {
                                var offset = lenTest + 1 + lenTest2;
                                while (lenEnd < cur + offset)
                                    _optimum[++lenEnd].Price = kIfinityPrice;
                                var curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                                var optimum = _optimum[cur + offset];
                                if (curAndLenPrice < optimum.Price) {
                                    optimum.Price = curAndLenPrice;
                                    optimum.PosPrev = cur + lenTest + 1;
                                    optimum.BackPrev = 0;
                                    optimum.Prev1IsChar = true;
                                    optimum.Prev2 = true;
                                    optimum.PosPrev2 = cur;
                                    optimum.BackPrev2 = repIndex;
                                }
                            }
                        }
                    }
                }

                if (newLen > numAvailableBytes) {
                    newLen = numAvailableBytes;
                    for (numDistancePairs = 0; newLen > _matchDistances[numDistancePairs]; numDistancePairs += 2) { }
                    _matchDistances[numDistancePairs] = newLen;
                    numDistancePairs += 2;
                }
                if (newLen >= startLen) {
                    normalMatchPrice = matchPrice + _isRep[state.Index].GetPrice0();
                    while (lenEnd < cur + newLen)
                        _optimum[++lenEnd].Price = kIfinityPrice;

                    uint offs = 0;
                    while (startLen > _matchDistances[offs])
                        offs += 2;

                    for (var lenTest = startLen; ; lenTest++) {
                        var curBack = _matchDistances[offs + 1];
                        var curAndLenPrice = normalMatchPrice + GetPosLenPrice(curBack, lenTest, posState);
                        var optimum = _optimum[cur + lenTest];
                        if (curAndLenPrice < optimum.Price) {
                            optimum.Price = curAndLenPrice;
                            optimum.PosPrev = cur;
                            optimum.BackPrev = curBack + Base.kNumRepDistances;
                            optimum.Prev1IsChar = false;
                        }

                        if (lenTest == _matchDistances[offs]) {
                            if (lenTest < numAvailableBytesFull) {
                                var t = Math.Min(numAvailableBytesFull - 1 - lenTest, _numFastBytes);
                                var lenTest2 = _matchFinder.GetMatchLen((int)lenTest, curBack, t);
                                if (lenTest2 >= 2) {
                                    var state2 = state;
                                    state2.UpdateMatch();
                                    var posStateNext = (position + lenTest) & _posStateMask;
                                    var curAndLenCharPrice = curAndLenPrice +
                                        _isMatch[(state2.Index << Base.kNumPosStatesBitsMax) + posStateNext].GetPrice0() +
                                        _literalEncoder.GetSubCoder(position + lenTest,
                                        _matchFinder.GetIndexByte((int)lenTest - 1 - 1)).
                                        GetPrice(true,
                                        _matchFinder.GetIndexByte((int)lenTest - (int)(curBack + 1) - 1),
                                        _matchFinder.GetIndexByte((int)lenTest - 1));
                                    state2.UpdateChar();
                                    posStateNext = (position + lenTest + 1) & _posStateMask;
                                    var nextMatchPrice = curAndLenCharPrice + _isMatch[(state2.Index << Base.kNumPosStatesBitsMax) + posStateNext].GetPrice1();
                                    var nextRepMatchPrice = nextMatchPrice + _isRep[state2.Index].GetPrice1();

                                    var offset = lenTest + 1 + lenTest2;
                                    while (lenEnd < cur + offset)
                                        _optimum[++lenEnd].Price = kIfinityPrice;
                                    curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                                    optimum = _optimum[cur + offset];
                                    if (curAndLenPrice < optimum.Price) {
                                        optimum.Price = curAndLenPrice;
                                        optimum.PosPrev = cur + lenTest + 1;
                                        optimum.BackPrev = 0;
                                        optimum.Prev1IsChar = true;
                                        optimum.Prev2 = true;
                                        optimum.PosPrev2 = cur;
                                        optimum.BackPrev2 = curBack + Base.kNumRepDistances;
                                    }
                                }
                            }
                            offs += 2;
                            if (offs == numDistancePairs)
                                break;
                        }
                    }
                }
            }
        }

        private void WriteEndMarker(uint posState) {
            if (!_writeEndMark)
                return;

            _isMatch[(_state.Index << Base.kNumPosStatesBitsMax) + posState].Encode(_rangeRangeCoder, 1);
            _isRep[_state.Index].Encode(_rangeRangeCoder, 0);
            _state.UpdateMatch();
            var len = Base.kMatchMinLen;
            _lenEncoder.Encode(_rangeRangeCoder, len - Base.kMatchMinLen, posState);
            uint posSlot = (1 << Base.kNumPosSlotBits) - 1;
            var lenToPosState = Base.GetLenToPosState(len);
            _posSlotEncoder[lenToPosState].Encode(_rangeRangeCoder, posSlot);
            var footerBits = 30;
            var posReduced = ((uint)1 << footerBits) - 1;
            _rangeRangeCoder.EncodeDirectBits(posReduced >> Base.kNumAlignBits, footerBits - Base.kNumAlignBits);
            _posAlignEncoder.ReverseEncode(_rangeRangeCoder, posReduced & Base.kAlignMask);
        }

        private void Flush(uint nowPos) {
            ReleaseMFStream();
            WriteEndMarker(nowPos & _posStateMask);
            _rangeRangeCoder.FlushData();
            _rangeRangeCoder.FlushStream();
        }

        public void CodeOneBlock(out long inSize, out long outSize, out bool finished) {
            inSize = 0;
            outSize = 0;
            finished = true;

            if (_inStream != null) {
                _matchFinder.SetStream(_inStream);
                _matchFinder.Init();
                _needReleaseMFStream = true;
                _inStream = null;
                if (_trainSize > 0)
                    _matchFinder.Skip(_trainSize);
            }

            if (_finished)
                return;
            _finished = true;


            var progressPosValuePrev = nowPos64;
            if (nowPos64 == 0) {
                if (_matchFinder.GetNumAvailableBytes() == 0) {
                    Flush((uint)nowPos64);
                    return;
                }
                uint len, numDistancePairs; // it's not used
                ReadMatchDistances(out len, out numDistancePairs);
                var posState = (uint)nowPos64 & _posStateMask;
                _isMatch[(_state.Index << Base.kNumPosStatesBitsMax) + posState].Encode(_rangeRangeCoder, 0);
                _state.UpdateChar();
                var curByte = _matchFinder.GetIndexByte((int)(0 - _additionalOffset));
                _literalEncoder.GetSubCoder((uint)nowPos64, _previousByte).Encode(_rangeRangeCoder, curByte);
                _previousByte = curByte;
                _additionalOffset--;
                nowPos64++;
            }
            if (_matchFinder.GetNumAvailableBytes() == 0) {
                Flush((uint)nowPos64);
                return;
            }
            while (true) {
                uint pos;
                var len = GetOptimum((uint)nowPos64, out pos);

                var posState = (uint)nowPos64 & _posStateMask;
                var complexState = (_state.Index << Base.kNumPosStatesBitsMax) + posState;
                if (len == 1 && pos == 0xFFFFFFFF) {
                    _isMatch[complexState].Encode(_rangeRangeCoder, 0);
                    var curByte = _matchFinder.GetIndexByte((int)(0 - _additionalOffset));
                    var subCoder = _literalEncoder.GetSubCoder((uint)nowPos64, _previousByte);
                    if (!_state.IsCharState()) {
                        var matchByte = _matchFinder.GetIndexByte((int)(0 - _repDistances[0] - 1 - _additionalOffset));
                        subCoder.EncodeMatched(_rangeRangeCoder, matchByte, curByte);
                    }
                    else
                        subCoder.Encode(_rangeRangeCoder, curByte);
                    _previousByte = curByte;
                    _state.UpdateChar();
                }
                else {
                    _isMatch[complexState].Encode(_rangeRangeCoder, 1);
                    if (pos < Base.kNumRepDistances) {
                        _isRep[_state.Index].Encode(_rangeRangeCoder, 1);
                        if (pos == 0) {
                            _isRepG0[_state.Index].Encode(_rangeRangeCoder, 0);
                            _isRep0Long[complexState].Encode(_rangeRangeCoder, len == 1 ? 0u : 1);
                        }
                        else {
                            _isRepG0[_state.Index].Encode(_rangeRangeCoder, 1);
                            if (pos == 1)
                                _isRepG1[_state.Index].Encode(_rangeRangeCoder, 0);
                            else {
                                _isRepG1[_state.Index].Encode(_rangeRangeCoder, 1);
                                _isRepG2[_state.Index].Encode(_rangeRangeCoder, pos - 2);
                            }
                        }
                        if (len == 1)
                            _state.UpdateShortRep();
                        else {
                            _repMatchLenEncoder.Encode(_rangeRangeCoder, len - Base.kMatchMinLen, posState);
                            _state.UpdateRep();
                        }
                        var distance = _repDistances[pos];
                        if (pos != 0) {
                            for (var i = pos; i >= 1; i--)
                                _repDistances[i] = _repDistances[i - 1];
                            _repDistances[0] = distance;
                        }
                    }
                    else {
                        _isRep[_state.Index].Encode(_rangeRangeCoder, 0);
                        _state.UpdateMatch();
                        _lenEncoder.Encode(_rangeRangeCoder, len - Base.kMatchMinLen, posState);
                        pos -= Base.kNumRepDistances;
                        var posSlot = GetPosSlot(pos);
                        var lenToPosState = Base.GetLenToPosState(len);
                        _posSlotEncoder[lenToPosState].Encode(_rangeRangeCoder, posSlot);

                        if (posSlot >= Base.kStartPosModelIndex) {
                            var footerBits = (int)((posSlot >> 1) - 1);
                            var baseVal = (2 | (posSlot & 1)) << footerBits;
                            var posReduced = pos - baseVal;

                            if (posSlot < Base.kEndPosModelIndex)
                                BitTreeEncoder.ReverseEncode(_posEncoders,
                                        baseVal - posSlot - 1, _rangeRangeCoder, footerBits, posReduced);
                            else {
                                _rangeRangeCoder.EncodeDirectBits(posReduced >> Base.kNumAlignBits, footerBits - Base.kNumAlignBits);
                                _posAlignEncoder.ReverseEncode(_rangeRangeCoder, posReduced & Base.kAlignMask);
                                _alignPriceCount++;
                            }
                        }
                        var distance = pos;
                        for (var i = Base.kNumRepDistances - 1; i >= 1; i--)
                            _repDistances[i] = _repDistances[i - 1];
                        _repDistances[0] = distance;
                        _matchPriceCount++;
                    }
                    _previousByte = _matchFinder.GetIndexByte((int)(len - 1 - _additionalOffset));
                }
                _additionalOffset -= len;
                nowPos64 += len;
                if (_additionalOffset == 0) {
                    // if (!_fastMode)
                    if (_matchPriceCount >= 1 << 7)
                        FillDistancesPrices();
                    if (_alignPriceCount >= Base.kAlignTableSize)
                        FillAlignPrices();
                    inSize = nowPos64;
                    outSize = _rangeRangeCoder.GetProcessedSizeAdd();
                    if (_matchFinder.GetNumAvailableBytes() == 0) {
                        Flush((uint)nowPos64);
                        return;
                    }

                    if (nowPos64 - progressPosValuePrev >= 1 << 12) {
                        _finished = false;
                        finished = false;
                        return;
                    }
                }
            }
        }

        private void ReleaseMFStream() {
            if (_matchFinder != null && _needReleaseMFStream) {
                _matchFinder.ReleaseStream();
                _needReleaseMFStream = false;
            }
        }


        private const int kPropSize = 5;
        private readonly byte[] _properties = new byte[kPropSize];

        public void WriteCoderProperties(Stream outStream) {
            _properties[0] = (byte)((_posStateBits * 5 + _numLiteralPosStateBits) * 9 + _numLiteralContextBits);
            for (var i = 0; i < 4; i++)
                _properties[1 + i] = (byte)((_dictionarySize >> (8 * i)) & 0xFF);
            outStream.Write(_properties, 0, kPropSize);
        }

        private readonly uint[] tempPrices = new uint[Base.kNumFullDistances];
        private uint _matchPriceCount;

        private void FillDistancesPrices() {
            for (var i = Base.kStartPosModelIndex; i < Base.kNumFullDistances; i++) {
                var posSlot = GetPosSlot(i);
                var footerBits = (int)((posSlot >> 1) - 1);
                var baseVal = (2 | (posSlot & 1)) << footerBits;
                tempPrices[i] = BitTreeEncoder.ReverseGetPrice(_posEncoders,
                    baseVal - posSlot - 1, footerBits, i - baseVal);
            }

            for (uint lenToPosState = 0; lenToPosState < Base.kNumLenToPosStates; lenToPosState++) {
                uint posSlot;
                var encoder = _posSlotEncoder[lenToPosState];

                var st = lenToPosState << Base.kNumPosSlotBits;
                for (posSlot = 0; posSlot < _distTableSize; posSlot++)
                    _posSlotPrices[st + posSlot] = encoder.GetPrice(posSlot);
                for (posSlot = Base.kEndPosModelIndex; posSlot < _distTableSize; posSlot++)
                    _posSlotPrices[st + posSlot] += ((posSlot >> 1) - 1 - Base.kNumAlignBits) << BitEncoder.kNumBitPriceShiftBits;

                var st2 = lenToPosState * Base.kNumFullDistances;
                uint i;
                for (i = 0; i < Base.kStartPosModelIndex; i++)
                    _distancesPrices[st2 + i] = _posSlotPrices[st + i];
                for (; i < Base.kNumFullDistances; i++)
                    _distancesPrices[st2 + i] = _posSlotPrices[st + GetPosSlot(i)] + tempPrices[i];
            }
            _matchPriceCount = 0;
        }

        private void FillAlignPrices() {
            for (uint i = 0; i < Base.kAlignTableSize; i++)
                _alignPrices[i] = _posAlignEncoder.ReverseGetPrice(i);
            _alignPriceCount = 0;
        }


        private static readonly string[] kMatchFinderIDs =
        {
            "Bt2",
            "BT4"
        };

        private static int FindMatchFinder(string s) {
            for (var m = 0; m < kMatchFinderIDs.Length; m++)
                if (s == kMatchFinderIDs[m])
                    return m;
            return -1;
        }

        public void SetCoderProperties(CoderPropID[] propIDs, object[] properties) {
            for (uint i = 0; i < properties.Length; i++) {
                var prop = properties[i];
                switch (propIDs[i]) {
                    case CoderPropID.NumFastBytes: {
                            if (!(prop is int))
                                throw new InvalidParamException();
                            var numFastBytes = (int)prop;
                            if (numFastBytes < 5 || numFastBytes > Base.kMatchMaxLen)
                                throw new InvalidParamException();
                            _numFastBytes = (uint)numFastBytes;
                            break;
                        }
                    case CoderPropID.Algorithm: {
                            /*
                            if (!(prop is Int32))
                                throw new InvalidParamException();
                            Int32 maximize = (Int32)prop;
                            _fastMode = (maximize == 0);
                            _maxMode = (maximize >= 2);
                            */
                            break;
                        }
                    case CoderPropID.MatchFinder: {
                            if (!(prop is string))
                                throw new InvalidParamException();
                            var matchFinderIndexPrev = _matchFinderType;
                            var m = FindMatchFinder(((string)prop).ToUpper());
                            if (m < 0)
                                throw new InvalidParamException();
                            _matchFinderType = (EMatchFinderType)m;
                            if (_matchFinder != null && matchFinderIndexPrev != _matchFinderType) {
                                _dictionarySizePrev = 0xFFFFFFFF;
                                _matchFinder = null;
                            }
                            break;
                        }
                    case CoderPropID.DictionarySize: {
                            const int kDicLogSizeMaxCompress = 30;
                            if (!(prop is int))
                                throw new InvalidParamException();
                        var dictionarySize = (int)prop;
                            if (dictionarySize < (uint)(1 << Base.kDicLogSizeMin) ||
                                dictionarySize > (uint)(1 << kDicLogSizeMaxCompress))
                                throw new InvalidParamException();
                            _dictionarySize = (uint)dictionarySize;
                            int dicLogSize;
                            for (dicLogSize = 0; dicLogSize < (uint)kDicLogSizeMaxCompress; dicLogSize++)
                                if (dictionarySize <= (uint)1 << dicLogSize)
                                    break;
                            _distTableSize = (uint)dicLogSize * 2;
                            break;
                        }
                    case CoderPropID.PosStateBits: {
                            if (!(prop is int))
                                throw new InvalidParamException();
                            var v = (int)prop;
                            if (v < 0 || v > (uint)Base.kNumPosStatesBitsEncodingMax)
                                throw new InvalidParamException();
                            _posStateBits = v;
                            _posStateMask = ((uint)1 << _posStateBits) - 1;
                            break;
                        }
                    case CoderPropID.LitPosBits: {
                            if (!(prop is int))
                                throw new InvalidParamException();
                            var v = (int)prop;
                            if (v < 0 || v > Base.kNumLitPosStatesBitsEncodingMax)
                                throw new InvalidParamException();
                            _numLiteralPosStateBits = v;
                            break;
                        }
                    case CoderPropID.LitContextBits: {
                            if (!(prop is int))
                                throw new InvalidParamException();
                            var v = (int)prop;
                            if (v < 0 || v > Base.kNumLitContextBitsMax)
                                throw new InvalidParamException(); ;
                            _numLiteralContextBits = v;
                            break;
                        }
                    case CoderPropID.EndMarker: {
                            if (!(prop is bool))
                                throw new InvalidParamException();
                            SetWriteEndMarkerMode((bool)prop);
                            break;
                        }
                    default:
                        throw new InvalidParamException();
                }
            }
        }

        private uint _trainSize;
        public void SetTrainSize(uint trainSize) {
            _trainSize = trainSize;
        }

    }
}
