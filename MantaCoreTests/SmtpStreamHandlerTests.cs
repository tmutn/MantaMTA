﻿using System.IO;
using MantaMTA.Core.Enums;
using MantaMTA.Core.Smtp;
using NUnit.Framework;

namespace MantaMTA.Core.Tests
{
	[TestFixture]
	public class SmtpStreamHandlerTests : TestFixtureBase
	{
		/// <summary>
		/// Test to make sure that SmtpStreamHandlers encoding is working correctly.
		/// </summary>
		[Test]
		public void TestUnicode()
		{
			string unicodeStr = "को कथा";
			using(MemoryStream ms = new MemoryStream())
			{
				SmtpStreamHandler stream = new SmtpStreamHandler(ms);
				stream.SetSmtpTransportMIME(SmtpTransportMIME._8BitUTF);
				stream.WriteAsync(unicodeStr, false).Wait();
				ms.Position = 0;
				string result = stream.ReadLineAsync(false).Result;
				Assert.AreEqual(unicodeStr, result);
			}

			using (MemoryStream ms = new MemoryStream())
			{
				SmtpStreamHandler stream = new SmtpStreamHandler(ms);
				stream.SetSmtpTransportMIME(SmtpTransportMIME._7BitASCII);
				stream.WriteLine(unicodeStr, false);
				ms.Position = 0;
				string result = stream.ReadLineAsync(false).Result;
				Assert.AreNotEqual(unicodeStr, result);
			}
		}
	}
}
