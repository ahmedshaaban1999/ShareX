﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2016 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Newtonsoft.Json;
using ShareX.HelpersLib;
using ShareX.UploadersLib.Properties;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ShareX.UploadersLib.FileUploaders
{
    public class DropboxFileUploaderService : FileUploaderService
    {
        public override FileDestination EnumValue { get; } = FileDestination.Dropbox;

        public override Icon ServiceIcon => Resources.Dropbox;

        public override bool CheckConfig(UploadersConfig config)
        {
            return OAuth2Info.CheckOAuth(config.DropboxOAuth2Info);
        }

        public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            return new Dropbox(config.DropboxOAuth2Info)
            {
                UploadPath = NameParser.Parse(NameParserType.URL, Dropbox.TidyUploadPath(config.DropboxUploadPath)),
                AutoCreateShareableLink = config.DropboxAutoCreateShareableLink,
                ShareURLType = config.DropboxURLType,
                AccountInfo = config.DropboxAccountInfo
            };
        }

        public override TabPage GetUploadersConfigTabPage(UploadersConfigForm form) => form.tpDropbox;
    }

    public enum DropboxURLType
    {
        Default,
        Shortened,
        Direct
    }

    public sealed class Dropbox : FileUploader, IOAuth2Basic
    {
        public OAuth2Info AuthInfo { get; set; }
        public DropboxAccount Account { get; set; }
        public DropboxAccountInfo AccountInfo { get; set; } // API v1
        public string UploadPath { get; set; }
        public bool AutoCreateShareableLink { get; set; }
        public DropboxURLType ShareURLType { get; set; }

        private const string APIVersion = "2";
        private const string Root = "dropbox";

        private const string URLWEB = "https://www.dropbox.com";
        private const string URLAPIBase = "https://api.dropboxapi.com";
        private const string URLAPI = URLAPIBase + "/" + APIVersion;
        private const string URLContent = "https://content.dropboxapi.com/" + APIVersion;
        private const string URLNotify = "https://notify.dropboxapi.com/" + APIVersion;

        private const string URLOAuth2Authorize = URLWEB + "/oauth2/authorize";
        private const string URLOAuth2Token = URLAPIBase + "/oauth2/token";

        private const string URLGetCurrentAccount = URLAPI + "/users/get_current_account";
        private const string URLDownload = URLContent + "/files/download";
        private const string URLUpload = URLContent + "/files/upload";
        private const string URLGetMetadata = URLAPI + "/files/get_metadata";
        private const string URLCreateSharedLink = URLAPI + "/sharing/create_shared_link_with_settings";
        private const string URLCopy = URLAPI + "/files/copy";
        private const string URLCreateFolder = URLAPI + "/files/create_folder";
        private const string URLDelete = URLAPI + "/files/delete";
        private const string URLMove = URLAPI + "/files/move";

        private const string URLPublicDirect = "https://dl.dropboxusercontent.com/u";
        private const string URLShareDirect = "https://dl.dropboxusercontent.com/s";

        public Dropbox(OAuth2Info oauth)
        {
            AuthInfo = oauth;
        }

        public string GetAuthorizationURL()
        {
            Dictionary<string, string> args = new Dictionary<string, string>();
            args.Add("response_type", "code");
            args.Add("client_id", AuthInfo.Client_ID);

            return CreateQuery(URLOAuth2Authorize, args);
        }

        public bool GetAccessToken(string code)
        {
            Dictionary<string, string> args = new Dictionary<string, string>();
            args.Add("client_id", AuthInfo.Client_ID);
            args.Add("client_secret", AuthInfo.Client_Secret);
            args.Add("grant_type", "authorization_code");
            args.Add("code", code);

            string response = SendRequest(HttpMethod.POST, URLOAuth2Token, args);

            if (!string.IsNullOrEmpty(response))
            {
                OAuth2Token token = JsonConvert.DeserializeObject<OAuth2Token>(response);

                if (token != null && !string.IsNullOrEmpty(token.access_token))
                {
                    AuthInfo.Token = token;
                    return true;
                }
            }

            return false;
        }

        private NameValueCollection GetAuthHeaders()
        {
            NameValueCollection headers = new NameValueCollection();
            headers.Add("Authorization", "Bearer " + AuthInfo.Token.access_token);
            return headers;
        }

        #region Dropbox accounts

        public DropboxAccount GetCurrentAccount()
        {
            DropboxAccount account = null;

            if (OAuth2Info.CheckOAuth(AuthInfo))
            {
                string response = SendRequestJSON(URLGetCurrentAccount, "null", GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    account = JsonConvert.DeserializeObject<DropboxAccount>(response);

                    if (account != null)
                    {
                        Account = account;
                    }
                }
            }

            return account;
        }

        #endregion Dropbox accounts

        #region Files and metadata

        public bool DownloadFile(string path, Stream downloadStream)
        {
            if (!string.IsNullOrEmpty(path) && OAuth2Info.CheckOAuth(AuthInfo))
            {
                NameValueCollection headers = GetAuthHeaders();

                path = URLHelpers.AddSlash(path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    path = path
                });

                headers.Add("Dropbox-API-Arg", arg);

                return SendRequest(HttpMethod.POST, downloadStream, URLDownload, headers: headers, contentType: ContentTypeJSON);
            }

            return false;
        }

        public UploadResult UploadFile(Stream stream, string path, string fileName, bool createShareableURL = false, DropboxURLType urlType = DropboxURLType.Default)
        {
            if (stream.Length > 150000000)
            {
                Errors.Add("There's a 150MB limit to uploads through the API.");
                return null;
            }

            NameValueCollection headers = GetAuthHeaders();

            path = URLHelpers.AddSlash(path, SlashType.Prefix);
            path = URLHelpers.CombineURL(path, fileName);

            string arg = JsonConvert.SerializeObject(new
            {
                path = path,
                mode = "overwrite",
                autorename = false,
                mute = true
            });

            headers.Add("Dropbox-API-Arg", arg);

            string response = SendRequestStream(URLUpload, stream, ContentTypeOctetStream, headers);

            UploadResult ur = new UploadResult(response);

            if (!string.IsNullOrEmpty(ur.Response))
            {
                DropboxMetadata metadata = JsonConvert.DeserializeObject<DropboxMetadata>(ur.Response);

                if (metadata != null)
                {
                    if (createShareableURL)
                    {
                        AllowReportProgress = false;
                        ur.URL = CreateShareableLink(metadata.path_display, urlType);
                    }
                    else
                    {
                        ur.URL = GetPublicURL(metadata.path_display);
                    }
                }
            }

            return ur;
        }

        public DropboxMetadata GetMetadata(string path)
        {
            DropboxMetadata metadata = null;

            if (OAuth2Info.CheckOAuth(AuthInfo))
            {
                path = URLHelpers.AddSlash(path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    path = path,
                    include_media_info = false,
                    include_deleted = false,
                    include_has_explicit_shared_members = false
                });

                string response = SendRequestJSON(URLGetMetadata, arg, GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    metadata = JsonConvert.DeserializeObject<DropboxMetadata>(response);
                }
            }

            return metadata;
        }

        public bool IsExists(string path)
        {
            DropboxMetadata metadata = GetMetadata(path);
            return metadata != null && !metadata.tag.Equals("deleted", StringComparison.InvariantCultureIgnoreCase);
        }

        public string CreateShareableLink(string path, DropboxURLType urlType)
        {
            if (!string.IsNullOrEmpty(path) && OAuth2Info.CheckOAuth(AuthInfo))
            {
                path = URLHelpers.AddSlash(path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    path = path,
                    settings = new
                    {
                        requested_visibility = "public" // Anyone who has received the link can access it. No login required.
                    }
                });

                // TODO: args.Add("short_url", urlType == DropboxURLType.Shortened ? "true" : "false");

                string response = SendRequestJSON(URLCreateSharedLink, arg, GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    DropboxLinkMetadata linkMetadata = JsonConvert.DeserializeObject<DropboxLinkMetadata>(response);

                    if (urlType == DropboxURLType.Direct)
                    {
                        Match match = Regex.Match(linkMetadata.url, @"https?://(?:www\.)?dropbox.com/s/(?<path>\w+/.+)");

                        if (match.Success)
                        {
                            string urlPath = match.Groups["path"].Value;

                            if (!string.IsNullOrEmpty(urlPath))
                            {
                                return URLHelpers.CombineURL(URLShareDirect, urlPath);
                            }
                        }
                    }
                    else
                    {
                        return linkMetadata.url;
                    }
                }
            }

            return null;
        }

        #endregion Files and metadata

        #region File operations

        public DropboxMetadata Copy(string from_path, string to_path)
        {
            DropboxMetadata metadata = null;

            if (!string.IsNullOrEmpty(from_path) && !string.IsNullOrEmpty(to_path) && OAuth2Info.CheckOAuth(AuthInfo))
            {
                from_path = URLHelpers.AddSlash(from_path, SlashType.Prefix);
                to_path = URLHelpers.AddSlash(to_path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    from_path = from_path,
                    to_path = to_path
                });

                string response = SendRequestJSON(URLCopy, arg, GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    metadata = JsonConvert.DeserializeObject<DropboxMetadata>(response);
                }
            }

            return metadata;
        }

        public DropboxMetadata CreateFolder(string path)
        {
            DropboxMetadata metadata = null;

            if (!string.IsNullOrEmpty(path) && OAuth2Info.CheckOAuth(AuthInfo))
            {
                path = URLHelpers.AddSlash(path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    path = path
                });

                string response = SendRequestJSON(URLCreateFolder, arg, GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    metadata = JsonConvert.DeserializeObject<DropboxMetadata>(response);
                }
            }

            return metadata;
        }

        public DropboxMetadata Delete(string path)
        {
            DropboxMetadata metadata = null;

            if (!string.IsNullOrEmpty(path) && OAuth2Info.CheckOAuth(AuthInfo))
            {
                path = URLHelpers.AddSlash(path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    path = path
                });

                string response = SendRequestJSON(URLDelete, arg, GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    metadata = JsonConvert.DeserializeObject<DropboxMetadata>(response);
                }
            }

            return metadata;
        }

        public DropboxMetadata Move(string from_path, string to_path)
        {
            DropboxMetadata metadata = null;

            if (!string.IsNullOrEmpty(from_path) && !string.IsNullOrEmpty(to_path) && OAuth2Info.CheckOAuth(AuthInfo))
            {
                from_path = URLHelpers.AddSlash(from_path, SlashType.Prefix);
                to_path = URLHelpers.AddSlash(to_path, SlashType.Prefix);

                string arg = JsonConvert.SerializeObject(new
                {
                    from_path = from_path,
                    to_path = to_path
                });

                string response = SendRequestJSON(URLMove, arg, GetAuthHeaders());

                if (!string.IsNullOrEmpty(response))
                {
                    metadata = JsonConvert.DeserializeObject<DropboxMetadata>(response);
                }
            }

            return metadata;
        }

        #endregion File operations

        public override UploadResult Upload(Stream stream, string fileName)
        {
            CheckEarlyURLCopy(UploadPath, fileName);

            return UploadFile(stream, UploadPath, fileName, AutoCreateShareableLink, ShareURLType);
        }

        private void CheckEarlyURLCopy(string path, string fileName)
        {
            if (OAuth2Info.CheckOAuth(AuthInfo) && !AutoCreateShareableLink)
            {
                string url = GetPublicURL(URLHelpers.CombineURL(path, fileName));
                OnEarlyURLCopyRequested(url);
            }
        }

        public string GetPublicURL(string path)
        {
            // TODO: uid
            return GetPublicURL(AccountInfo.Uid.ToString(), path);
        }

        public static string GetPublicURL(string userID, string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                path = path.Trim('/');

                if (path.StartsWith("Public/", StringComparison.InvariantCultureIgnoreCase))
                {
                    path = URLHelpers.URLPathEncode(path.Substring(7));
                    return URLHelpers.CombineURL(URLPublicDirect, userID, path);
                }
            }

            return "Upload path is private. Use \"Public\" folder to get public URL.";
        }

        public static string TidyUploadPath(string uploadPath)
        {
            if (!string.IsNullOrEmpty(uploadPath))
            {
                return uploadPath.Trim().Replace('\\', '/').Trim('/') + "/";
            }

            return "";
        }
    }

    public class DropboxAccount
    {
        public string account_id { get; set; }
        public DropboxAccountName name { get; set; }
        public string email { get; set; }
        public bool email_verified { get; set; }
        public bool disabled { get; set; }
        public string locale { get; set; }
        public string referral_link { get; set; }
        public bool is_paired { get; set; }
        public DropboxAccountType account_type { get; set; }
        public string profile_photo_url { get; set; }
        public string country { get; set; }
    }

    public class DropboxAccountName
    {
        public string given_name { get; set; }
        public string surname { get; set; }
        public string familiar_name { get; set; }
        public string display_name { get; set; }
    }

    public class DropboxAccountType
    {
        [JsonProperty(".tag")]
        public string tag { get; set; }
    }

    public class DropboxMetadata
    {
        [JsonProperty(".tag")]
        public string tag { get; set; }
        public string name { get; set; }
        public string id { get; set; }
        public string client_modified { get; set; }
        public string server_modified { get; set; }
        public string rev { get; set; }
        public int size { get; set; }
        public string path_lower { get; set; }
        public string path_display { get; set; }
        public DropboxMetadataSharingInfo sharing_info { get; set; }
        public List<DropboxMetadataPropertyGroup> property_groups { get; set; }
        public bool has_explicit_shared_members { get; set; }
    }

    public class DropboxMetadataSharingInfo
    {
        public bool read_only { get; set; }
        public string parent_shared_folder_id { get; set; }
        public string modified_by { get; set; }
    }

    public class DropboxMetadataPropertyGroup
    {
        public string template_id { get; set; }
        public List<DropboxMetadataPropertyGroupField> fields { get; set; }
    }

    public class DropboxMetadataPropertyGroupField
    {
        public string name { get; set; }
        public string value { get; set; }
    }

    public class DropboxLinkMetadata
    {
        [JsonProperty(".tag")]
        public string tag { get; set; }
        public string url { get; set; }
        public string name { get; set; }
        public DropboxLinkMetadataPermissions link_permissions { get; set; }
        public string client_modified { get; set; }
        public string server_modified { get; set; }
        public string rev { get; set; }
        public int size { get; set; }
        public string id { get; set; }
        public string path_lower { get; set; }
        public DropboxLinkMetadataTeamMemberInfo team_member_info { get; set; }
    }

    public class DropboxLinkMetadataPermissions
    {
        public bool can_revoke { get; set; }
        public DropboxLinkMetadataResolvedVisibility resolved_visibility { get; set; }
        public DropboxLinkMetadataRevokeFailureReason revoke_failure_reason { get; set; }
    }

    public class DropboxLinkMetadataResolvedVisibility
    {
        [JsonProperty(".tag")]
        public string tag { get; set; }
    }

    public class DropboxLinkMetadataRevokeFailureReason
    {
        [JsonProperty(".tag")]
        public string tag { get; set; }
    }

    public class DropboxLinkMetadataTeamMemberInfo
    {
        public DropboxLinkMetadataTeamInfo team_info { get; set; }
        public string display_name { get; set; }
        public string member_id { get; set; }
    }

    public class DropboxLinkMetadataTeamInfo
    {
        public string id { get; set; }
        public string name { get; set; }
    }

    #region API v1

    public class DropboxAccountInfo
    {
        public string Referral_link { get; set; }
        public string Display_name { get; set; }
        public long Uid { get; set; }
        public string Country { get; set; }
        public DropboxQuotaInfo Quota_info { get; set; }
        public string Email { get; set; }
    }

    public class DropboxQuotaInfo
    {
        public long Normal { get; set; }
        public long Shared { get; set; }
        public long Quota { get; set; }
    }

    #endregion API v1
}