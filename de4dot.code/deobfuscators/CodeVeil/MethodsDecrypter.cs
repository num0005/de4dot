﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dnlib.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.CodeVeil {
	class MethodsDecrypter {
		MainType mainType;
		IDecrypter decrypter;

		interface IDecrypter {
			void initialize(byte[] methodsData);
			bool decrypt(IBinaryReader fileDataReader, DumpedMethod dm);
		}

		class Decrypter : IDecrypter {
			IBinaryReader methodsDataReader;

			public virtual void initialize(byte[] methodsData) {
				methodsDataReader = MemoryImageStream.Create(methodsData);
			}

			public virtual bool decrypt(IBinaryReader fileDataReader, DumpedMethod dm) {
				if (fileDataReader.ReadByte() != 0x2A)
					return false;	// Not a RET
				methodsDataReader.Position = fileDataReader.ReadCompressedUInt32();

				dm.mhCodeSize = methodsDataReader.ReadCompressedUInt32();
				dm.code = methodsDataReader.ReadBytes((int)dm.mhCodeSize);
				if ((dm.mhFlags & 8) != 0)
					dm.extraSections = MethodBodyParser.readExtraSections(methodsDataReader);

				if (!decryptCode(dm))
					return false;

				return true;
			}

			protected virtual bool decryptCode(DumpedMethod dm) {
				return true;
			}
		}

		class DecrypterV5 : Decrypter {
			byte[] decryptKey;

			public override void initialize(byte[] methodsData) {
				var data = DeobUtils.inflate(methodsData, true);
				decryptKey = BitConverter.GetBytes(BitConverter.ToUInt32(data, 0));

				var newMethodsData = new byte[data.Length - 4];
				Array.Copy(data, 4, newMethodsData, 0, newMethodsData.Length);
				base.initialize(newMethodsData);
			}

			protected override bool decryptCode(DumpedMethod dm) {
				var code = dm.code;
				for (int i = 0; i < code.Length; i++) {
					for (int j = 0; j < 4 && i + j < code.Length; j++)
						code[i + j] ^= decryptKey[j];
				}

				return true;
			}
		}

		public bool Detected {
			get { return decrypter != null; }
		}

		public MethodsDecrypter(MainType mainType) {
			this.mainType = mainType;
		}

		public MethodsDecrypter(MainType mainType, MethodsDecrypter oldOne) {
			this.mainType = mainType;
		}

		public void find() {
			if (!mainType.Detected)
				return;

			switch (mainType.Version) {
			case ObfuscatorVersion.Unknown:
				break;

			case ObfuscatorVersion.V3:
			case ObfuscatorVersion.V4_0:
			case ObfuscatorVersion.V4_1:
				decrypter = new Decrypter();
				break;

			case ObfuscatorVersion.V5_0:
				decrypter = new DecrypterV5();
				break;

			default:
				throw new ApplicationException("Unknown version");
			}
		}

		public bool decrypt(byte[] fileData, ref DumpedMethods dumpedMethods) {
			if (decrypter == null)
				return false;

			using (var peImage = new MyPEImage(fileData)) {
				if (peImage.Sections.Count <= 0)
					return false;

				var methodsData = findMethodsData(peImage, fileData);
				if (methodsData == null)
					return false;

				decrypter.initialize(methodsData);

				dumpedMethods = createDumpedMethods(peImage, fileData, methodsData);
				if (dumpedMethods == null)
					return false;
			}

			return true;
		}

		DumpedMethods createDumpedMethods(MyPEImage peImage, byte[] fileData, byte[] methodsData) {
			var dumpedMethods = new DumpedMethods();

			var methodsDataReader = MemoryImageStream.Create(methodsData);
			var fileDataReader = MemoryImageStream.Create(fileData);

			var methodDef = peImage.DotNetFile.MetaData.TablesStream.MethodTable;
			for (uint rid = 1; rid <= methodDef.Rows; rid++) {
				var dm = new DumpedMethod();

				peImage.readMethodTableRowTo(dm, rid);
				if (dm.mdRVA == 0)
					continue;
				uint bodyOffset = peImage.rvaToOffset(dm.mdRVA);

				byte b = peImage.offsetReadByte(bodyOffset);
				uint codeOffset;
				if ((b & 3) == 2) {
					if (b != 2)
						continue;	// not zero byte code size

					dm.mhFlags = 2;
					dm.mhMaxStack = 8;
					dm.mhLocalVarSigTok = 0;
					codeOffset = bodyOffset + 1;
				}
				else {
					if (peImage.offsetReadUInt32(bodyOffset + 4) != 0)
						continue;	// not zero byte code size

					dm.mhFlags = peImage.offsetReadUInt16(bodyOffset);
					dm.mhMaxStack = peImage.offsetReadUInt16(bodyOffset + 2);
					dm.mhLocalVarSigTok = peImage.offsetReadUInt32(bodyOffset + 8);
					codeOffset = bodyOffset + (uint)(dm.mhFlags >> 12) * 4;
				}
				fileDataReader.Position = codeOffset;

				if (!decrypter.decrypt(fileDataReader, dm))
					continue;

				dumpedMethods.add(dm);
			}

			return dumpedMethods;
		}

		// xor eax, eax / inc eax / pop esi edi edx ecx ebx / leave / ret 0Ch or 10h
		static byte[] initializeMethodEnd = new byte[] {
			0x33, 0xC0, 0x40, 0x5E, 0x5F, 0x5A, 0x59, 0x5B, 0xC9, 0xC2,
		};
		byte[] findMethodsData(MyPEImage peImage, byte[] fileData) {
			var section = peImage.Sections[0];

			var reader = MemoryImageStream.Create(fileData);

			const int RVA_EXECUTIVE_OFFSET = 1 * 4;
			const int ENC_CODE_OFFSET = 6 * 4;
			int lastOffset = Math.Min(fileData.Length, (int)(section.PointerToRawData + section.SizeOfRawData));
			for (int offset = getStartOffset(peImage); offset < lastOffset; ) {
				offset = findSig(fileData, offset, lastOffset, initializeMethodEnd);
				if (offset < 0)
					return null;
				offset += initializeMethodEnd.Length;

				short retImm16 = BitConverter.ToInt16(fileData, offset);
				if (retImm16 != 0x0C && retImm16 != 0x10)
					continue;
				offset += 2;
				if (offset + ENC_CODE_OFFSET + 4 > lastOffset)
					return null;

				// rva is 0 when the assembly has been embedded
				uint rva = BitConverter.ToUInt32(fileData, offset + RVA_EXECUTIVE_OFFSET);
				if (rva != 0 && mainType.Rvas.IndexOf(rva) < 0)
					continue;

				int relOffs = BitConverter.ToInt32(fileData, offset + ENC_CODE_OFFSET);
				if (relOffs <= 0 || relOffs >= section.SizeOfRawData)
					continue;
				reader.Position = section.PointerToRawData + relOffs;

				int size = (int)reader.ReadCompressedUInt32();
				int endOffset = relOffs + size;
				if (endOffset < relOffs || endOffset > section.SizeOfRawData)
					continue;

				return reader.ReadBytes(size);
			}

			return null;
		}

		int getStartOffset(MyPEImage peImage) {
			int minOffset = int.MaxValue;
			foreach (var rva in mainType.Rvas) {
				int rvaOffs = (int)peImage.rvaToOffset((uint)rva);
				if (rvaOffs < minOffset)
					minOffset = rvaOffs;
			}
			return minOffset == int.MaxValue ? 0 : minOffset;
		}

		static int findSig(byte[] fileData, int offset, int lastOffset, byte[] sig) {
			for (int i = offset; i < lastOffset - sig.Length + 1; i++) {
				if (fileData[i] != sig[0])
					continue;
				if (compare(fileData, i + 1, sig, 1, sig.Length - 1))
					return i;
			}
			return -1;
		}

		static bool compare(byte[] a1, int i1, byte[] a2, int i2, int len) {
			for (int i = 0; i < len; i++) {
				if (a1[i1++] != a2[i2++])
					return false;
			}
			return true;
		}
	}
}
