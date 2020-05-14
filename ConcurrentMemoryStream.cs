using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PNServer.Core.Network
{
    public unsafe class ConcurrentMemoryStream 
    {
        const int _controlInfoSize = sizeof(int);

        IntPtr _hndl;

        IntPtr _writeptr;
        IntPtr _readptr;
        IntPtr _endPtr;

        public ConcurrentMemoryStream(IntPtr _bufferSize)
        {
            _hndl = Marshal.AllocHGlobal(_bufferSize);

            _writeptr = _hndl;
            _readptr = _hndl;
            _endPtr = (IntPtr)((long)_hndl + (long)_bufferSize);

            zeromemoryimpl((byte*)_hndl, _bufferSize.ToInt64());

        }

        ~ConcurrentMemoryStream()
        {
            Marshal.FreeHGlobal(_hndl);
        }

        public static unsafe void memcpyimpl(byte* src, byte* dest, int len)
        {
            if (len == 1)
                *dest = *src;
            else if ((len & 0x01) != 0)
            {
                ___memcpyimpl(src, dest, len - 1);
                *(dest + (len - 1)) = *(src + (len - 1));
            }
            else
                ___memcpyimpl(src, dest, len);
        }
        internal static unsafe void zeromemoryimpl(byte* dest, long len)
        {
            if (len == 1)
                *dest = 0;
            else if ((len & 0x01) != 0)
            {
                ___zeroMemory(dest, len - 1);
                *(dest + (len - 1)) = 0;
            }
            else
                ___zeroMemory(dest, len);
        }
        internal static unsafe void ___zeroMemory(byte* dest, long len)
        {
            if (len >= 0x10)
            {
                do
                {
                    *((long*)dest) = 0;
                    *((long*)(dest + 8)) = 0;
                    dest += 0x10;
                }
                while ((len -= 0x10) >= 0x10);
            }
            if (len > 0)
            {
                if ((len & 8) != 0)
                {
                    *((long*)dest) = 0;
                    dest += 8;
                }
                if ((len & 4) != 0)
                {
                    *((int*)dest) = 0;
                    dest += 4;
                }
                if ((len & 2) != 0)
                {
                    *((short*)dest) = 0;
                    dest += 2;
                }
                if ((len & 1) != 0)
                {
                    dest++;
                    dest[0] = 0;
                }
            }
        }
        internal static unsafe void zeromemoryimpl(byte* dest, int len)
        {
            if (len == 1)
                *dest = 0;
            else if ((len & 0x01) != 0)
            {
                ___zeroMemory(dest, len - 1);
                *(dest + (len - 1)) = 0;
            }
            else
                ___zeroMemory(dest, len);
        }
        internal static unsafe void ___zeroMemory(byte* dest, int len)
        {
            if (len >= 0x10)
            {
                do
                {
                    *((long*)dest) = 0;
                    *((long*)(dest + 8)) = 0;
                    dest += 0x10;
                }
                while ((len -= 0x10) >= 0x10);
            }
            if (len > 0)
            {
                if ((len & 8) != 0)
                {
                    *((long*)dest) = 0;
                    dest += 8;
                }
                if ((len & 4) != 0)
                {
                    *((int*)dest) = 0;
                    dest += 4;
                }
                if ((len & 2) != 0)
                {
                    *((short*)dest) = 0;
                    dest += 2;
                }
                if ((len & 1) != 0)
                {
                    dest++;
                    dest[0] = 0;
                }
            }
        }
        internal static unsafe void ___memcpyimpl(byte* src, byte* dest, int len)
        {
            if (len >= 0x10)
            {
                do
                {
                    *((long*)dest) = *((long*)src);
                    //*((int*)(dest + 4)) = *((int*)(src + 4));
                    *((long*)(dest + 8)) = *((long*)(src + 8));
                    // *((int*)(dest + 12)) = *((int*)(src + 12));
                    dest += 0x10;
                    src += 0x10;
                }
                while ((len -= 0x10) >= 0x10);
            }
            if (len > 0)
            {
                if ((len & 8) != 0)
                {
                    *((long*)dest) = *((long*)src);
                    //    *((int*)(dest + 4)) = *((int*)(src + 4));
                    dest += 8;
                    src += 8;
                }
                if ((len & 4) != 0)
                {
                    *((int*)dest) = *((int*)src);
                    dest += 4;
                    src += 4;
                }
                if ((len & 2) != 0)
                {
                    *((short*)dest) = *((short*)src);
                    dest += 2;
                    src += 2;
                }
                if ((len & 1) != 0)
                {
                    dest++;
                    src++;
                    dest[0] = src[0];
                }
            }
        }

        public void DoubleWrite(byte *bf_1, int bf_1Size, byte *bf_2, int bf_2Size)
        {
            SpinWait sw = new SpinWait();
            IntPtr head;

            int count_bytes = bf_1Size + bf_2Size;

            do
            {
                head = _writeptr;
                IntPtr rslt = head + _controlInfoSize + count_bytes;

                if ((long)rslt > (long)_endPtr)
                {
                    sw.SpinOnce(sleep1Threshold: -1);
                    continue;
                }

                if (Interlocked.CompareExchange(ref _writeptr, rslt, head) == head)
                    break;

                sw.SpinOnce(sleep1Threshold: -1);
            } while (true);

            memcpyimpl((byte*)bf_1, (byte*)(head + _controlInfoSize), bf_1Size);
            memcpyimpl((byte*)bf_2, (byte*)(head + _controlInfoSize + bf_1Size), bf_2Size);
            Interlocked.MemoryBarrier();
            *(int*)head = (int)(count_bytes | 0x80000000);

        }
        public void Write(byte* buffer, int count_bytes)
        {
            SpinWait sw = new SpinWait();
            IntPtr head;

            do
            {
                head = _writeptr;
                IntPtr rslt = head + _controlInfoSize + count_bytes;

                if ((long)rslt > (long)_endPtr)
                {
                    sw.SpinOnce(sleep1Threshold: -1);
                    continue;
                }

                if (Interlocked.CompareExchange(ref _writeptr, rslt, head) == head)
                    break;

                sw.SpinOnce(sleep1Threshold: -1);
            } while (true);

            

            memcpyimpl((byte*)buffer, (byte*)(head + _controlInfoSize), count_bytes);
            Interlocked.MemoryBarrier();
            *(int*)head = (int)(count_bytes | 0x80000000);
        }
        public int Read(byte* _OutPut)
        {
            if (_writeptr == _readptr)
            {
                IntPtr _r = Interlocked.CompareExchange(ref _writeptr, _hndl, _readptr);

                if (_r == _readptr)
                {
                    _readptr = _hndl;
                    return 0;
                }
                else
                    return Read(_OutPut);
            }

            SpinWait sw = new SpinWait();
            int control_info;
            do
            {
                control_info = *(int*)_readptr;

                byte flag = (byte)((control_info & 0x80000000) >> 31);

                if (flag == 1)
                    break;

                sw.SpinOnce(sleep1Threshold: -1);
            } while (true);
            
            control_info &= 0x7ffffff;
            memcpyimpl((byte*)(_readptr + _controlInfoSize), _OutPut, control_info);
            zeromemoryimpl((byte*)_readptr, control_info + _controlInfoSize);
            _readptr += control_info + _controlInfoSize;
            return control_info;
        }
    }
}
