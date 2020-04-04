﻿using FoenixIDE.MemoryLocations;
using FoenixIDE.Simulator.Devices.SDCard;
using System;
using System.Collections.Generic;
using System.IO;

namespace FoenixIDE.Simulator.Devices
{
    internal class ShortLongFileName
    {
        public string shortName;
        public string longName;
        public bool isDirectory = false;
    }

    public class CH376SRegister: SDCardDevice
    {
        private CH376SCommand currentCommand = CH376SCommand.NONE;
        
        string filename = "";
        string fileToReadAsBytes = null;
        string spaces = "\0\0\0\0\0\0\0\0";

        List <ShortLongFileName> dircontent = new List<ShortLongFileName> ();
        int dirItem = 0;
        string filedata = "";
        int filepos = -1;
        int fileoffset = 0;
        
        int byteRead = 0;
        byte[] byteReadArray = new byte[4];
        byte[] fileArray;

        public CH376SRegister(int StartAddress, int Length): base(StartAddress, Length)
        {
        }

       

        public override byte ReadByte(int Address)
        {
            if (Address == 0 && currentCommand == CH376SCommand.RD_USB_DATA0)
            {
                if (fileToReadAsBytes == null)
                {
                    if (filepos > -1 && filepos < filedata.Length)
                    {
                        return (byte)filedata[filepos++];
                    }
                    filepos = 0;
                }
                else
                {
                    // Return a byte from the file array buffer
                    if (filepos == -1)
                    {
                        filepos = 0;
                    }
                    else
                    {
                        return fileArray[fileoffset + filepos++];
                    }
                }
            }
            return base.ReadByte(Address);
        }
        public override void WriteByte(int Address, byte Value)
        {
            data[Address] = Value;
            switch (Address)
            {
                case 0:
                    switch (currentCommand)
                    {
                        case CH376SCommand.CHECK_EXIST:
                            data[0] = (byte)~Value; // Return the complement
                            break;
                        case CH376SCommand.SET_USB_MODE:
                            if (isPresent)
                            {
                                data[0] = (byte)CH376SResponse.CMD_RET_SUCCESS;
                            }
                            else
                            {
                                data[0] = (byte)CH376SResponse.CMD_RET_ABORT;
                            }
                            break;
                        
                        case CH376SCommand.FILE_OPEN:
                            break;
                        case CH376SCommand.SET_FILE_NAME:
                            if (Value != 0)
                            {
                                filename += (Char)Value;
                            }
                            break;
                        case CH376SCommand.BYTE_READ:
                            if (byteRead == -1)
                            {
                                // generated by the interrupt
                                byteRead = 0;
                            }
                            else
                            {
                                byteReadArray[byteRead] = data[0];
                                if (byteRead == 1)
                                {
                                    // read the file in an array
                                    fileArray = File.ReadAllBytes(fileToReadAsBytes);
                                    filepos = -1;
                                }
                                data[0] = (byte)CH376SInterrupt.USB_INT_DISK_READ;
                                byteRead++;
                            }
                            break;
                    }
                    break;
                case 1:
                    currentCommand = (CH376SCommand)Value;
                    switch (currentCommand)
                    {
                        case CH376SCommand.DISK_MOUNT:
                            // Set the interrupt
                            sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_SUCCESS);
                            break;
                        case CH376SCommand.SET_FILE_NAME:
                            //sdCardIRQMethod?.Invoke(SDCardInterrupt.USB_INT_SUCCESS);
                            filename = "";
                            break;
                        case CH376SCommand.FILE_OPEN:
                            sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_DISK_READ);
                            ReadFile(filename);
                            dirItem = 0;
                            break;
                        case CH376SCommand.FILE_CLOSE:
                            sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_DISK_READ);
                            sdCurrentPath = "";
                            break;
                        case CH376SCommand.RD_USB_DATA0:
                            if (fileToReadAsBytes == null)
                            {
                                filedata = dircontent[dirItem++].shortName;
                                data[0] = (byte)filedata.Length;
                                filepos = -1;
                            }
                            else
                            {
                                // I'm not sure what I'm supposed to write here - is this the file length?
                                // Try reading 255 bytes from the file at a time
                                if (fileArray.Length - fileoffset > 255)
                                {
                                    data[0] = 255;
                                }
                                else
                                {
                                    data[0] = (byte)(fileArray.Length - fileoffset);
                                }
                                filepos = -1;
                            }
                            break;
                        case CH376SCommand.FILE_ENUM_GO:
                            if (dirItem < dircontent.Count)
                            {
                                sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_DISK_READ);
                            }
                            else
                            {
                                sdCardIRQMethod?.Invoke(CH376SInterrupt.ERR_MISS_FIL);
                            }
                            filepos = -1;
                            break;
                        case CH376SCommand.GET_STATUS:
                            break;
                        case CH376SCommand.BYTE_READ:
                            byteRead = -1;
                            fileoffset = 0;
                            sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_DISK_READ);
                            break;
                        case CH376SCommand.BYTE_RD_GO:
                            fileoffset += 255;
                            if (filepos + fileoffset >= fileArray.Length)
                            {
                                sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_SUCCESS);
                            }
                            else
                            {
                                sdCardIRQMethod?.Invoke(CH376SInterrupt.USB_INT_DISK_READ);
                            }
                            break;
                    }
                    break;
                case 9:
                    break;
            }
        }


        /* __________________________________________________________________________________________________________
         * | 00 - 07 | 08 - 0A |  	0B     |     0C    |     0D     | 0E  -  0F | 10  -  11 | 12 - 13|  14 - 15 | 16 - 17 | 18 - 19 |   1A - 1B   |  1C  -  1F |
         * |Filename |Extension|File attrib|User attrib|First ch del|Create time|Create date|Owner ID|Acc rights|Mod. time|Mod. date|Start cluster|  File size |
         * ++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
         */
        private void ReadFile(string name)
        {
            if (name.Contains("*"))
            {
                fileToReadAsBytes = null;
                // Path is compounded, as long as "CLOSE" is not called
                string rootPath = GetSDCardPath();
                if (sdCurrentPath.Length > 0)
                {
                    rootPath = sdCurrentPath;
                }
                
                dircontent.Clear();
                LoadDirContents(rootPath);
            }
            else
            {
                string fileToOpen = name;
                if (fileToOpen.StartsWith("/"))
                {
                    fileToOpen = fileToOpen.Substring(1).Replace("/*", "");
                }
                // we only store the name of the file, not the path
                int lastSlash = fileToOpen.LastIndexOf("/");
                if (lastSlash > 0)
                {
                    fileToOpen = fileToOpen.Substring(lastSlash + 1);
                }
                ShortLongFileName slf = FindByShortName(fileToOpen);
                if (slf != null)
                {
                    if (slf.isDirectory)
                    {
                        sdCurrentPath = slf.longName;
                    }
                    else
                    {
                        dircontent.Clear();
                        dircontent.Add(slf);
                        fileToReadAsBytes = slf.longName;
                        data[0] = (byte)CH376SResponse.CMD_STAT_SUCCESS;
                    }
                }
            }
        }

        private string ShortFilename(string longname)
        {
            int pos = longname.IndexOf('.');
            if (pos > 0)
            {
                string filename = longname.Substring(0, pos).Replace(" ", "").Replace("\\", "");
                string extension = longname.Substring(pos+1);
                if (filename.Length > 8)
                {
                    filename = filename.Substring(0, 6) + "~1";
                }
                filename += spaces.Substring(0, 8 - filename.Length);
                if (extension.Length > 3)
                {
                    extension = extension.Substring(0, 3);
                }
                extension += spaces.Substring(0,3 - extension.Length);
                return filename.ToUpper() + extension.ToUpper();
            }
            else
            {
                string filename = longname.Replace(" ", "").Replace("\\","");
                if (filename.Length > 8)
                {
                    filename = filename.Substring(0, 6) + "~1";
                }
                filename += spaces.Substring(0,8-filename.Length);
                return filename.ToUpper() + spaces.Substring(0, 3);
            }
        }

        private bool ListContains(List<ShortLongFileName> directory, string shortname)
        {
            foreach (ShortLongFileName slf in directory)
            {
                if (slf.shortName.Substring(0, 11).Equals(shortname))
                {
                    return true;
                }
            }
            return false;
        }

        private ShortLongFileName FindByShortName(string name)
        {
            string shortName = name.Replace(".", "");
            if (name.Equals(".."))
            {
                shortName = "..";
            }
            foreach (ShortLongFileName slf in dircontent)
            {
                string partial = slf.shortName.Substring(0, 11).Replace("\0", "");
                if (partial.Equals(shortName))
                {
                    return slf;
                }
            }
            return null;
        }

        private void LoadDirContents(string path)
        {
            string[] dirs = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
            string[] files = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);

            // Add the parent folder only if the inital name is not /*
            if (!path.Equals(GetSDCardPath()))
            {
                ShortLongFileName slf = new ShortLongFileName();
                DirectoryInfo parent = Directory.GetParent(path);
                slf.longName = parent.ToString();
                slf.shortName = ".." + spaces + spaces[0] + (char)(byte)FileAttributes.Directory + spaces + spaces + "\0\0\0\0";
                slf.isDirectory = true;
                dircontent.Add(slf);
            }
            foreach (string dir in dirs)
            {
                ShortLongFileName slf = new ShortLongFileName
                {
                    longName = dir,
                    shortName = ShortFilename(dir.Substring(path.Length)) + (char)(byte)FileAttributes.Directory + spaces + spaces + "\0\0\0\0",
                    isDirectory = true
                };
                dircontent.Add(slf);
            }
            foreach (string file in files)
            {
                int size = (int)new FileInfo(file).Length;
                byte[] sizeB = BitConverter.GetBytes(size);

                ShortLongFileName slf = new ShortLongFileName
                {
                    longName = file,
                    shortName = ShortFilename(file.Substring(path.Length)) + (char)(byte)FileAttributes.Archive + spaces + spaces + System.Text.Encoding.Default.GetString(sizeB)
                };
                while (ListContains(dircontent, slf.shortName.Substring(0, 11)))
                {
                    if (slf.shortName.Substring(7, 1).Equals("\0"))
                    {
                        break;
                    }

                    int fileVal = Convert.ToInt32(slf.shortName.Substring(7, 1));
                    fileVal++;
                    slf.shortName = slf.shortName.Substring(0, 7) + fileVal + slf.shortName.Substring(8);
                }
                dircontent.Add(slf);
            }
        }
    }
}