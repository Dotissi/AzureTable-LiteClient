﻿using Dotissi.AzureTable.LiteClient.Exceptions;
using Dotissi.AzureTable.LiteClient.Http;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Dotissi.AzureTable.LiteClient
{
   
    public class AzureTable:IDisposable
    {
        HttpClient httpClient;
        RequestBuilder requestBuilder;
        Signature signature;
        string uri;
        string table;
        internal AzureTable(string uri,string account,string key,string table)
        {
            this.uri = uri.TrimEnd('/').TrimEnd('\\');
            httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(this.uri);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            this.requestBuilder = new RequestBuilder(this.uri);
            this.signature = new Signature(account, key);
            this.table = table;
        }
        
        public async Task<List<T>> FindAllAsync<T>( string filter=null, string select=null)
        {
            var paramss = this.BuildReadParams(filter, select);
            List<T> all = new List<T>();
            bool needReadNext;
            do
            {
                var httpResponseMessage =await ExecuteOneReadAsync<T>(all,paramss);
                IEnumerable<string> nextPKs;
                IEnumerable<string> nextRKs;
                bool continuationFoundPK = httpResponseMessage.Headers.TryGetValues("x-ms-continuation-NextPartitionKey", out nextPKs);
                bool continuationFoundRK = httpResponseMessage.Headers.TryGetValues("x-ms-continuation-NextRowKey", out nextRKs);
                needReadNext = false;
                if (continuationFoundPK)
                {
                    var pk = nextPKs.FirstOrDefault();
                    if (!string.IsNullOrEmpty(pk))
                    {
                        paramss["NextPartitionKey"] = pk;
                        needReadNext = true;
                    }
                    var rk = nextRKs.FirstOrDefault();
                    if (!string.IsNullOrEmpty(rk))
                    {
                        paramss["NextRowKey"] = rk;
                    }
                   
                }
               

            } while (needReadNext);

            return all;
           
        }
        public async Task<List<T>> FindAsync<T>(string filter = null, string select = null, string top = null)
        {
            var paramss = this.BuildReadParams(filter,select,top);
            List<T> all = new List<T>();

            await ExecuteOneReadAsync<T>(all, paramss);

            return all;

        }
        private Dictionary<string, string> BuildReadParams(string filter = null, string select = null, string top = null)
        {
            Dictionary<string, string> paramss = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(filter))
            {
                paramss.Add("$filter", filter);
            }
            if (!string.IsNullOrEmpty(select))
            {
                paramss.Add("$select", select);
            }
            if (!string.IsNullOrEmpty(top))
            {
                paramss.Add("$top", top);
            }
            return paramss;
        }
        private async Task<HttpResponseMessage> ExecuteOneReadAsync<T>(List<T> all, Dictionary<string, string> paramss)
        {
            string uriFragment = table + "()";
            HttpRequestMessage request = requestBuilder.BuildGetRequest(uriFragment, paramss);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            var obj = await httpResponseMessage.Content.ReadAsStringAsync();

            JObject d = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(obj);
            JArray array = d.GetValue("value") as JArray;

            foreach (var item in array)
            {
                all.Add(item.ToObject<T>());
            }
            return httpResponseMessage;
        }

        public async Task<T> FindOneAsync<T>(string partitionKey,string rowKey)
        {
            string uriFragment = string.Format("{0}(PartitionKey='{1}', RowKey='{2}')", table, partitionKey, rowKey); ;
            HttpRequestMessage request = requestBuilder.BuildGetRequest(uriFragment, null);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            if (httpResponseMessage == null)
                return default(T);

            var obj = await httpResponseMessage.Content.ReadAsStringAsync();

            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(obj);
            
         
        }

        public async Task<TableResult> InsertAsync(string table, object entity)
        {
            string uriFragment = table;
            HttpRequestMessage request = requestBuilder.BuildSubmitRequest(uriFragment, entity,HTTPSubmitType.POST);
            request.Headers.Add("Prefer", "return-no-content");
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            return PrepareSubmitResult(httpResponseMessage);
        }
       
        public Task<TableResult> InsertOrMergeAsync(string table, object entity)
        {
            return this.InsertOrUpdateAsync(table, entity, HTTPSubmitType.MERGE);
        }
        public Task<TableResult> InsertOrReplaceAsync(string table, object entity)
        {
            return this.InsertOrUpdateAsync(table, entity, HTTPSubmitType.PUT);
        }
        private async Task<TableResult> InsertOrUpdateAsync(string table, object entity, HTTPSubmitType submitType)
        {
            var uriFragment = PrepareUriFragment(table, entity);
            HttpRequestMessage request = requestBuilder.BuildSubmitRequest(uriFragment, entity, submitType);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            return PrepareSubmitResult(httpResponseMessage);
        }
        public Task<TableResult> ReplaceAsync(string table, string Etag, object entity)
        {
            return this.UpdateAsync(table, Etag, entity, HTTPSubmitType.PUT);
        }
        public Task<TableResult> MergeAsync(string table, string Etag, object entity)
        {
            return this.UpdateAsync(table, Etag, entity, HTTPSubmitType.MERGE);
        }

        private async Task<TableResult> UpdateAsync(string table,string Etag, object entity, HTTPSubmitType submitType)
        {
           
            var uriFragment = PrepareUriFragment(table, entity);
            HttpRequestMessage request = requestBuilder.BuildSubmitRequest(uriFragment, entity, submitType);
            request.Headers.Add("If-Match", Etag);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            return PrepareSubmitResult(httpResponseMessage);
        }

        public async Task<TableResult> DeleteAsync(string table, string Etag, object entity)
        {
            var uriFragment = PrepareUriFragment(table, entity);
            HttpRequestMessage request = requestBuilder.BuildDeleteRequest(uriFragment);
            request.Headers.Add("If-Match", Etag);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            TableResult tr = new TableResult();
            tr.HttpStatusCode = (int)httpResponseMessage.StatusCode;

            return tr;
        }
        public async Task DropTableAsync()
        {
            var uriFragment = string.Format("Tables('{0}')", this.table);
            HttpRequestMessage request = requestBuilder.BuildDeleteRequest(uriFragment);

            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
        }
        public async Task CreateTableAsync()
        {
            var uriFragment = "Tables";
            HttpRequestMessage request = requestBuilder.BuildSubmitRequest(uriFragment, new { TableName = this.table },HTTPSubmitType.POST);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
        }
        public async Task<bool> ExistsTableAsync()
        {
            var uriFragment = string.Format("Tables('{0}')",table);
            HttpRequestMessage request = requestBuilder.BuildGetRequest(uriFragment, null);
            HttpResponseMessage httpResponseMessage = await this.SendAsync(request);
            return httpResponseMessage != null;
        }
        private string PrepareUriFragment(string table, object entity)
        {
            var pk = ReflectionHelper.GetPropertyStringValue(entity, "PartitionKey");
            var rk = ReflectionHelper.GetPropertyStringValue(entity, "RowKey");
            return string.Format("{0}(PartitionKey='{1}', RowKey='{2}')", table, pk, rk);
        }
        private TableResult PrepareSubmitResult(HttpResponseMessage httpResponseMessage)
        {
            TableResult tr = new TableResult();
            tr.ETag = httpResponseMessage.Headers.ETag.ToString();
            tr.HttpStatusCode = (int)httpResponseMessage.StatusCode;
            tr.Timestamp = ParseETagForTimestamp(tr.ETag);
            return tr;
        }

        private static DateTimeOffset ParseETagForTimestamp(string etag)
        {
            if (etag.StartsWith("W/", StringComparison.Ordinal))
            {
                etag = etag.Substring(2);
            }
            string prefix = "\"datetime'";
            etag = etag.Substring(prefix.Length, etag.Length - 2 - prefix.Length);
            return DateTimeOffset.Parse(Uri.UnescapeDataString(etag), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }
        private static string GetETagFromTimestamp(string timeStampString)
        {
            timeStampString = Uri.EscapeDataString(timeStampString);
            return "W/\"datetime'" + timeStampString + "'\"";
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            signature.SignMessage(request);
            var response = await httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorDesc = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (errorDesc.Contains(@"""code"":""ResourceNotFound"""))
                        return null;
                }
                StorageException ex = new StorageException(response.ReasonPhrase + "("+(int)response.StatusCode+")")
                { ErrorDescription = errorDesc, StatusCode = response.StatusCode };
                throw ex;
            

            }
            return response;
        }
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.httpClient.Dispose();
            }
        }
    }
    internal enum HTTPSubmitType { PUT, POST, MERGE }
}
