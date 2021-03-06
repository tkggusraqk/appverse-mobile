/*
 Copyright (c) 2012 GFT Appverse, S.L., Sociedad Unipersonal.

 This Source  Code Form  is subject to the  terms of  the Appverse Public License 
 Version 2.0  (“APL v2.0”).  If a copy of  the APL  was not  distributed with this 
 file, You can obtain one at http://appverse.org/legal/appverse-license/.

 Redistribution and use in  source and binary forms, with or without modification, 
 are permitted provided that the  conditions  of the  AppVerse Public License v2.0 
 are met.

 THIS SOFTWARE IS PROVIDED BY THE  COPYRIGHT HOLDERS  AND CONTRIBUTORS "AS IS" AND
 ANY EXPRESS  OR IMPLIED WARRANTIES, INCLUDING, BUT  NOT LIMITED TO,   THE IMPLIED
 WARRANTIES   OF  MERCHANTABILITY   AND   FITNESS   FOR A PARTICULAR  PURPOSE  ARE
 DISCLAIMED. EXCEPT IN CASE OF WILLFUL MISCONDUCT OR GROSS NEGLIGENCE, IN NO EVENT
 SHALL THE  COPYRIGHT OWNER  OR  CONTRIBUTORS  BE LIABLE FOR ANY DIRECT, INDIRECT,
 INCIDENTAL,  SPECIAL,   EXEMPLARY,  OR CONSEQUENTIAL DAMAGES  (INCLUDING, BUT NOT
 LIMITED TO,  PROCUREMENT OF SUBSTITUTE  GOODS OR SERVICES;  LOSS OF USE, DATA, OR
 PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT(INCLUDING NEGLIGENCE OR OTHERWISE) 
 ARISING  IN  ANY WAY OUT  OF THE USE  OF THIS  SOFTWARE,  EVEN  IF ADVISED OF THE 
 POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoTouch.Foundation;
using MonoTouch.UIKit;
using Unity.Core.IO.ScriptSerialization;
using Unity.Core.System;
using System.Threading;
using ICSharpCode.SharpZipLib.Zip;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Unity.Platform.IPhone
{

	public class IPhoneUtils
	{
		private static string _ResourcesZipped = "$ResourcesZipped$";
		private static string APP_RESOURCES_ZIP = "app-encrypted.zip";
		private static string ENCRYPTION_PASSWORD = "$hashB$";
		private ZipFile _zipFile = null;
		private string[] _zipEntriesNames = null;
		private Dictionary<string, ZipEntry> _zipEntries = null; 
		private JavaScriptSerializer Serialiser = null;

		private static IPhoneUtils singleton = null;
		private IPhoneUtils() : base() {
		}

		public bool ResourcesZipped { 
			get {

				bool result = false;
				try {
					result = Boolean.Parse(IPhoneUtils._ResourcesZipped);
				} catch (Exception) {}

				return result;
			} 
		}

		private void LoadZippedFile() {
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();

			try {

				// 0. Get zip resources as binary data (resources are encrypted)
				byte[] input = GetResourceAsBinaryFromFile(APP_RESOURCES_ZIP);
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "# input length: " + input.Length);

				//int BLOCK_SIZE = 128;
				//int KEY_SIZE = 128;
				int SALT_LENGTH = 8;
				int SALTED_LENGTH = SALT_LENGTH * 2;

				// 1. Remove the 8 byte salt

				byte[] salt =  new byte[SALT_LENGTH];
				Buffer.BlockCopy(input, SALT_LENGTH, salt, 0, SALT_LENGTH);
				
				// 2. Remove salt from file data
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "Salt: " + BitConverter.ToString(salt).Replace("-",""));
				
				int aesDataLength = input.Length - SALTED_LENGTH;
				byte[] aesData = new byte[aesDataLength];
				Buffer.BlockCopy(input, SALTED_LENGTH, aesData, 0, aesDataLength);

				// 3. Create Key and IV from password
				byte[] password = Encoding.UTF8.GetBytes(ENCRYPTION_PASSWORD);
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "password: " + Encoding.UTF8.GetString(password));
				
				MD5 md5 = MD5.Create();
				
				int preKeyLength = password.Length + salt.Length;
				byte[] preKey = new byte[preKeyLength];
				
				Buffer.BlockCopy(password, 0, preKey, 0, password.Length);
				Buffer.BlockCopy(salt, 0, preKey, password.Length, salt.Length);
				
				byte[] key = md5.ComputeHash(preKey);
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "key: " + BitConverter.ToString(key).Replace("-",""));
				
				int preIVLength = key.Length + preKeyLength;
				byte[] preIV = new byte[preIVLength];
				
				Buffer.BlockCopy(key, 0, preIV, 0, key.Length);
				Buffer.BlockCopy(preKey, 0, preIV, key.Length, preKey.Length);

				byte[] iv = md5.ComputeHash(preIV);
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "iv: " + BitConverter.ToString(iv).Replace("-",""));

				md5.Clear();
				md5 = null;

				// 4. Decrypt using AES
				Rijndael rijndael = Rijndael.Create();
				rijndael.Padding = PaddingMode.None;
				CryptoStream cs = new CryptoStream(new MemoryStream(input), rijndael.CreateDecryptor(key, iv), CryptoStreamMode.Read);
				MemoryStream ms = new MemoryStream();
				cs.CopyTo(ms);
				cs.Close();
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "decrypted ms length: " + ms.Length);

				_zipFile = new ZipFile(ms); // it is more efficient to create the ZipFile object using an Stream (from NSData), instead of using the file path.

				List<string> entries = new List<string>();
				_zipEntries = new Dictionary<string, ZipEntry>();
				foreach(ZipEntry entry in _zipFile) {
					//SystemLogger.Log(SystemLogger.Module.PLATFORM, "# entry found: " + entry.Name);
					entries.Add(entry.Name);
					// storing ZipEntry in a HashMap or similar to avoid unnecessary loops looking for the index!
					_zipEntries.Add(entry.Name, entry);
				}
				_zipEntriesNames = entries.ToArray();
			
			} catch (Exception ex) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Exception loading resources as zipped file: " + ex.Message, ex);
			}

			stopwatch.Stop();
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Time elapsed loading zipped file: "+ stopwatch.Elapsed);
		}
		
		public static IPhoneUtils GetInstance() {
			if (singleton==null) {
				singleton = new IPhoneUtils();

				singleton.Serialiser = new JavaScriptSerializer ();

				if(singleton.ResourcesZipped) {
					// testing removing disk and memory cache capacity
					SystemLogger.Log (SystemLogger.Module.PLATFORM, "# Removing cache disk and memory capacity");
					NSUrlCache.SharedCache.DiskCapacity = 0;
					NSUrlCache.SharedCache.MemoryCapacity = 0;
					singleton.LoadZippedFile();
				}
			}
			return singleton;
		}

		public string[] GetFilesFromDirectory(string directoryPath, string filePattern) {
			if(this.ResourcesZipped) {

				// get relative path (removing application bundle path)
				directoryPath = this.GetFileFullPath(directoryPath);
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "# getting files entries from directory: " + directoryPath);

				List<string> files = new List<string>();
				foreach(string entry in _zipEntriesNames) {
					if(entry.StartsWith(directoryPath) && entry.EndsWith(Path.GetExtension(filePattern)))
						files.Add(entry);
				}
				return files.ToArray();
			} else {
				return Directory.GetFiles (directoryPath, filePattern);
			}
		}


		public bool ResourceExists(string resourcePath) {
			//SystemLogger.Log (SystemLogger.Module.PLATFORM, "# Checking file exists: " + resourcePath);
			if(this.ResourcesZipped) {
				return IPhoneUtils.GetInstance().ResourceFromZippedExists(resourcePath);
			} else {
				return File.Exists(resourcePath);
			}
		}

		private bool ResourceFromZippedExists(string resourcePath) {

			// get relative path (removing application bundle path)
			resourcePath = this.GetFileFullPath(resourcePath);

			//SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Checking resource exists in zipped file: " + resourcePath);
			if (_zipFile != null)  {
				return (this.GetZipEntry (resourcePath)!=null);
			}

			return false;
		}

		/// <summary>
		/// Gets the zip entry.
		/// </summary>
		/// <returns>
		/// The zip entry.
		/// </returns>
		/// <param name='entryName'>
		/// Entry name.
		/// </param>
		private ZipEntry GetZipEntry(string entryName) {
			if(_zipEntries != null && _zipEntries.ContainsKey(entryName))  {
				return _zipEntries[entryName];
			} else {
				return null;
			}
		}

		private byte[] GetResourceAsBinaryFromZipped(string resourcePath) {
			if (_zipFile != null)  {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Loading resource from zipped file: " + resourcePath);
				//Stopwatch stopwatch = new Stopwatch();
				//stopwatch.Start();

				ZipEntry entry = this.GetZipEntry(resourcePath);  // getting the entry from the _zipFile.GetEntry() method is less efficient
				if(entry != null) {
					//SystemLogger.Log(SystemLogger.Module.PLATFORM, "# entry found [" + entry.Name + "]: " + entry.Size);

					//long et1 = stopwatch.ElapsedMilliseconds;
					Stream entryStream = _zipFile.GetInputStream(entry);

					//long et2 = stopwatch.ElapsedMilliseconds;

					// entryStream is not seekable, it should be first readed
					byte[] data = IPhoneUtils.ConvertNonSeekableStreamToByteArray(entryStream); //, entry.Size
					//SystemLogger.Log(SystemLogger.Module.PLATFORM, "# entry found [" + entry.Name + "], data byte array size:" + data.Length);

					//long et3 = stopwatch.ElapsedMilliseconds;
					//SystemLogger.Log(SystemLogger.Module.PLATFORM, "CSV," + resourcePath + "," + entry.Size + "," + entry.CompressedSize + ","+ et1 +","+(et2-et1)+","+(et3-et2)+","+(et3));
					//stopwatch.Stop();
					return data;
				}

				//stopwatch.Stop();
			}
			
			return null;
		}

		private Stream GetResourceStreamFromZipped(string resourcePath) {
			byte[] data = GetResourceAsBinaryFromZipped(resourcePath);
			if(data != null) {
				return new MemoryStream(data);
			} else {
				return null;
			}
		}

		public static byte[] ConvertNonSeekableStreamToByteArray(Stream nonSeeakable) {
			if(nonSeeakable is MemoryStream) {
				return ((MemoryStream)nonSeeakable).ToArray();
			} else {
				using(MemoryStream ms = new MemoryStream()) {
					nonSeeakable.CopyTo(ms, 32*1024);
					return ms.ToArray();
				}
			}

		}


		public NSUrl GetNSUrlFromPath(string path) {
			NSUrl nsUrl = null;
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Getting nsurl from path: " + path);
			try {
				if(this.ResourcesZipped) {
					//path = this.GetFileFullPath(path);
					SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Getting nsurl from path inside zipped file: " + path);
					byte[] result = this.GetResourceAsBinaryFromZipped(path);

					if(result!=null) {
						string documentsPath = Environment.GetFolderPath (Environment.SpecialFolder.MyDocuments);
						string tmpPath = Path.Combine (documentsPath, "..", "tmp");
						string filename = Path.Combine (tmpPath, Path.GetFileName(path));
						File.WriteAllBytes(filename, result);
						NSFileManager.SetSkipBackupAttribute (filename, true); // backup will be skipped for this file
						path = filename;
						SystemLogger.Log(SystemLogger.Module.PLATFORM, "# storing file on temporal path for displaying: " + path);
					} else {
						SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Resource not found inside app respurces zipped file: " + path);
					}

					// check resource directly from local file system
					nsUrl = NSUrl.FromFilename(path);
				} else {
					// check resource directly from local file system
					nsUrl = NSUrl.FromFilename(path);
				}

			} catch (Exception e) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Error trying to get nsurl from file [" + path +"]", e);
			}
			
			return nsUrl;
		}
		
		public string GetFileFullPath(string filePath) {
			if(this.ResourcesZipped) {
				string bundlePath = NSBundle.MainBundle.BundlePath;
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Checking resource exists in zipped file (bundlePath): " + bundlePath);
				if(filePath.IndexOf(bundlePath)>=0) {
					filePath = filePath.Substring(filePath.IndexOf(bundlePath) + bundlePath.Length + 1); // +1 for removing leading path separator
				}
				//sSystemLogger.Log(SystemLogger.Module.PLATFORM, "# Checking resource exists in zipped file (filePath): " + filePath);
				return filePath;
			} else {
				return Path.Combine(NSBundle.MainBundle.BundlePath, filePath);
			}
		}

		private Stream GetResourceAsStreamFromFile(string resourcePath) 
		{
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Loading resource from file: " + resourcePath);
			
			MemoryStream bufferStream = new MemoryStream();
			//Monitor.Enter(this);
			
			try {
				
				NSData data = NSData.FromFile(resourcePath);
				byte[] buffer = new byte[data.Length];
				Marshal.Copy(data.Bytes, buffer,0,buffer.Length);
				bufferStream.Write(buffer,0,buffer.Length);
				
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "Resource Stream length:" + bufferStream.Length);
				
			} catch (Exception ex) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "Error trying to get file stream [" + resourcePath +"]", ex);
			}
			//Monitor.Exit(this);
			return bufferStream;
		}
		
		public Stream GetResourceAsStream(string resourcePath) 
		{
			if(this.ResourcesZipped) {
				// get relative path (removing application bundle path)
				resourcePath = this.GetFileFullPath(resourcePath);

				return this.GetResourceStreamFromZipped(resourcePath);
			} else {
				return this.GetResourceAsStreamFromFile(resourcePath);
			}
		}

		public byte[] GetResourceAsBinary(string resourcePath, bool isWebResource)  {
			if(this.ResourcesZipped && isWebResource) {
				// get relative path (removing application bundle path)
				resourcePath = this.GetFileFullPath(resourcePath);
				
				return this.GetResourceAsBinaryFromZipped(resourcePath);
			} else {
				return this.GetResourceAsBinaryFromFile(resourcePath);
			}
		}
		
		private byte[] GetResourceAsBinaryFromFile(string resourcePath) 
		{
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "# Loading resource from file: " + resourcePath);

			try {
				//Stopwatch stopwatch = new Stopwatch();
				//stopwatch.Start();

				NSData data = NSData.FromFile(resourcePath);

				if(data == null) {
					SystemLogger.Log(SystemLogger.Module.PLATFORM, "File not available at path: " + resourcePath);
				} else {
					byte[] buffer = new byte[data.Length];
					Marshal.Copy(data.Bytes, buffer,0,buffer.Length);

					return buffer;
				}

				//stopwatch.Stop();
				//SystemLogger.Log(SystemLogger.Module.PLATFORM, "CSV not-zipped," + resourcePath + ","+ stopwatch.ElapsedMilliseconds);

			} catch (Exception ex) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "Error trying to get file binary data [" + resourcePath +"]", ex);
			}

			return new byte[0];
		}
		
		public bool IsIPad() {
			IOperatingSystem iOS = (IOperatingSystem) IPhoneServiceLocator.GetInstance ().GetService ("system");
			HardwareInfo hInfo =  iOS.GetOSHardwareInfo();
			if(hInfo != null && hInfo.Version != null && hInfo.Version.Contains("iPad")) {
				return true;
			}
			
			return false;
		}
		
		public string GetUserAgent() {
			IOperatingSystem iOS = (IOperatingSystem) IPhoneServiceLocator.GetInstance ().GetService ("system");
			return iOS.GetOSUserAgent();
		}
		
		
		public bool ShouldAutorotate() {
			try {
				IDisplay service = (IDisplay) IPhoneServiceLocator.GetInstance().GetService("system");
				return !service.IsOrientationLocked();
			} catch (Exception ex) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "Error trying to determine if application should autorotate", ex);
			}
			return true; // by default
		}

		public Dictionary<String,Object> ConvertToDictionary(NSMutableDictionary dictionary) {
			Dictionary<String,Object> prunedDictionary = new Dictionary<String,Object>();
			foreach (NSString key in dictionary.Keys) { 
				NSObject dicValue = dictionary.ObjectForKey(key);  

				if (dicValue is NSDictionary) {
					prunedDictionary.Add (key.ToString(), ConvertToDictionary(new NSMutableDictionary(dicValue as NSDictionary)));
				} else {
					//SystemLogger.Log(SystemLogger.Module.PLATFORM, "***** key["+key.ToString ()+"] is instance of: " + dicValue.GetType().FullName);
					if ( ! (dicValue is NSNull)) {
						if(dicValue is NSString) {
							prunedDictionary.Add (key.ToString(), ((NSString)dicValue).Description);
						} else if(dicValue is NSNumber) {
							prunedDictionary.Add (key.ToString(), ((NSNumber)dicValue).IntValue);
						} else if(dicValue is NSArray) {
							prunedDictionary.Add (key.ToString(), ConvertToArray((NSArray)dicValue));
						} else {
							prunedDictionary.Add (key.ToString(), dicValue);
						}
					} 
				}
			}
			return prunedDictionary;
		}

		public NSDictionary ConvertToNSDictionary(Dictionary<String,Object> dictionary) {
			NSMutableDictionary prunedDictionary = new NSMutableDictionary();
			foreach (String key in dictionary.Keys) { 
				Object dicValue = dictionary[key];  
				
				if (dicValue is Dictionary<String,Object>) {
					prunedDictionary.Add (new NSString(key), ConvertToNSDictionary((dicValue as Dictionary<String,Object>)));
				} else {
					//SystemLogger.Log(SystemLogger.Module.PLATFORM, "***** key["+key+"] is instance of: " + dicValue.GetType().FullName);
					if (dicValue != null) {
						if(dicValue is String) {
							prunedDictionary.Add (new NSString(key), new NSString((dicValue as String)));
						} else if(dicValue is int) {
							prunedDictionary.Add (new NSString(key), new NSNumber((int) dicValue));
						} else if(dicValue is Object[]) {
							prunedDictionary.Add (new NSString(key), ConvertToNSArray((Object[])dicValue));
						} else if(dicValue is System.Collections.ArrayList) {
							prunedDictionary.Add (new NSString(key), ConvertToNSArray((dicValue as System.Collections.ArrayList).ToArray()));
						}else {
							SystemLogger.Log(SystemLogger.Module.PLATFORM, "*** exception parsing key["+key+"] instance of: " + dicValue.GetType().FullName +". No complex object are valid inside this dictionary");
						}
					} 
				}
			}
			return prunedDictionary;
		}

		private Object[] ConvertToArray(NSArray nsArray) {
			Object[] arr = new Object[nsArray.Count];
			for (uint i = 0; i < nsArray.Count; i++) {
				var o = MonoTouch.ObjCRuntime.Runtime.GetNSObject(nsArray.ValueAt(i));
				if(o is NSArray) {
					arr[i] = ConvertToArray((o as NSArray));
				} else if (o is NSDictionary) {

				} else if (o is NSMutableDictionary) {
					arr[i] = ConvertToDictionary((o as NSMutableDictionary));
				} if(o is NSString) {
					arr[i] = (o as NSString).Description;
				} else if(o is NSNumber) {
					arr[i] = (o as NSNumber).IntValue;
				}
			}

			return arr;
		}

		private NSArray ConvertToNSArray(Object[] nsArray) {
			return NSArray.FromObjects(nsArray);
		}

		public String JSONSerialize(object data) {
			try {

				return Serialiser.Serialize(data);
			} catch (Exception ex) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "Error trying to serialize object to JSON.", ex);
			}
			return null;
		}

		public T JSONDeserialize<T> (string json) {
			try {
				return Serialiser.Deserialize<T>(json);
			} catch (Exception ex) {
				SystemLogger.Log(SystemLogger.Module.PLATFORM, "Error trying to serialize object to JSON.", ex);
			}
			return default(T);
		}

		public void FireUnityJavascriptEvent (string method, object data)
		{
			string dataJSONString = Serialiser.Serialize (data);
			if (data is String) {
				dataJSONString = "'"+ (data as String) +"'";
			}
			string jsCallbackFunction = "if("+method+"){"+method+"("+dataJSONString+");}";
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "NotifyJavascript (single object): " + method + ", dataJSONString: " + dataJSONString);
			IPhoneServiceLocator.CurrentDelegate.MainUIWebView().EvaluateJavascript(jsCallbackFunction);
			SystemLogger.Log (SystemLogger.Module.PLATFORM, "NotifyJavascript EVALUATED");
		}

		public void FireUnityJavascriptEvent (string method, object[] dataArray)
		{
			StringBuilder builder = new StringBuilder();
			int numObjects = 0;
			foreach(object data in dataArray) {
				if(numObjects>0) {
					builder.Append(",");
				}
				if (data == null) {
					builder.Append("null");
				}
				if (data is String) {
					builder.Append("'"+ (data as String) +"'");
				} else {
					builder.Append(Serialiser.Serialize (data));
				}
				numObjects++;
			}
			string dataJSONString = builder.ToString();

			string jsCallbackFunction = "if("+method+"){"+method+"("+dataJSONString+");}";
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "NotifyJavascript (array object): " + method + ", dataJSONString: " + dataJSONString); // + ",jsCallbackFunction="+ jsCallbackFunction 
			IPhoneServiceLocator.CurrentDelegate.MainUIWebView().EvaluateJavascript(jsCallbackFunction);
			SystemLogger.Log (SystemLogger.Module.PLATFORM, "NotifyJavascript EVALUATED");
		}

		public void ExecuteJavascriptCallback(string callbackFunction, string id, string jsonResultString) {
			string jsCallbackFunction = "try{if("+callbackFunction+"){"+callbackFunction+"("+jsonResultString+", '"+ id +"');}}catch(e) {console.log('error executing javascript callback: ' + e)}";
			SystemLogger.Log(SystemLogger.Module.PLATFORM, "ExecuteJavascriptCallback: " + callbackFunction);
			IPhoneServiceLocator.CurrentDelegate.MainUIWebView().EvaluateJavascript(jsCallbackFunction);
		}
	}
}
