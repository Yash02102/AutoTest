class RedisHash
{
    public Task<bool> DeleteKeyAsync(string key, CommandFlags flags = CommandFlags.None)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
            return _db.KeyDeleteAsync(key, flags);
        }
}

class priceExtension
{
    public static async Task RemoveStaleCache(this IContextService contextService, ICacheProvider _cacheProvider, IQuotePriceHashCache quotePriceHashCache, string cartId)
        {
            await RemoveStaleCache(contextService, _cacheProvider, cartId);

            if (quotePriceHashCache != null)
            {
                await quotePriceHashCache.InvalidateAsync(cartId);
            }
        }
}

namespace PricingService.Manager.Infra.Cache
{
    public interface IQuotePriceHashCache
    {
        Task<byte[]> GetFullAsync(string quoteId);

        Task<Item> GetItemAsync(string quoteId, string itemId);

        Task<QuotePriceHashShipment> GetShipmentAsync(string quoteId, string shipmentId);

        Task SetFullAsync(string quoteId, byte[] compressedPrices);

        Task SetAsync(string quoteId, IQuoteSalesOrderPrice quotePrice, byte[] compressedPrices);

        Task InvalidateAsync(string quoteId);

        Task DeleteItemAsync(string quoteId, string itemId);

        Task DeleteShipmentAsync(string quoteId, string shipmentId);
    }
}


namespace PricingService.Manager.Infra.Cache
{
    public class QuotePriceHashShipment
    {
        public string Id { get; set; }
        public ShipmentPriceSummary PriceSummary { get; set; }
        public List<CategoryGroup> ReportCategories { get; set; }
        public List<string> ItemIds { get; set; }
    }
}


namespace Dell.DSA.PricingService.Manager.Modules.Cache
{
    public class QuotePriceHashCache : IQuotePriceHashCache
    {
        private const string QuotePriceHashKeyPrefix = "quote:price:v2:hash";

        private static class RedisHashFields
        {
            public const string Full = "full";
            public const string Summary = "summary";
            public const string ItemsIndex = "items:index";
            public const string ShipmentsIndex = "shipments:index";
        }

        private readonly IRedisHash _redisHash;
        private readonly IContextService _contextService;
        private readonly ApplicationSettings _applicationSettings;

        public QuotePriceHashCache(IRedisHash redisHash, IContextService contextService, IOptionsMonitor<ApplicationSettings> applicationSettings)
        {
            _redisHash = redisHash;
            _contextService = contextService;
            _applicationSettings = applicationSettings.CurrentValue;
        }

        public Task<byte[]> GetFullAsync(string quoteId)
        {
            return _redisHash.GetAsync<byte[]>(GetHashKey(quoteId), RedisHashFields.Full);
        }

        public Task<Item> GetItemAsync(string quoteId, string itemId)
        {
            return _redisHash.GetAsync<Item>(GetHashKey(quoteId), GetItemField(itemId));
        }

        public Task<QuotePriceHashShipment> GetShipmentAsync(string quoteId, string shipmentId)
        {
            return _redisHash.GetAsync<QuotePriceHashShipment>(GetHashKey(quoteId), GetShipmentField(shipmentId));
        }

        public Task SetFullAsync(string quoteId, byte[] compressedPrices)
        {
            return _redisHash.SetAsync(GetHashKey(quoteId), RedisHashFields.Full, compressedPrices, GetPriceCacheTtl());
        }

        public async Task SetAsync(string quoteId, IQuoteSalesOrderPrice quotePrice, byte[] compressedPrices)
        {
            if (quotePrice == null || compressedPrices == null) return;

            var hashKey = GetHashKey(quoteId);
            var ttl = GetPriceCacheTtl();

            await _redisHash.SetAsync(hashKey, RedisHashFields.Full, compressedPrices, ttl);

            if (quotePrice.PricingSummary != null)
            {
                await _redisHash.SetAsync(hashKey, RedisHashFields.Summary, quotePrice.PricingSummary, ttl);
            }

            var shipments = quotePrice.Shipments ?? new List<Shipment>();
            var shipmentIndex = new List<string>();
            var shipmentEntries = new Dictionary<string, QuotePriceHashShipment>(StringComparer.Ordinal);

            foreach (var shipment in shipments)
            {
                if (string.IsNullOrWhiteSpace(shipment?.Id)) continue;

                shipmentIndex.Add(shipment.Id);

                var itemIds = shipment.Items?
                    .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                    .Select(i => i.Id)
                    .ToList() ?? new List<string>();

                shipmentEntries[GetShipmentField(shipment.Id)] = new QuotePriceHashShipment
                {
                    Id = shipment.Id,
                    PriceSummary = shipment.PriceSummary,
                    ReportCategories = shipment.ReportCategories,
                    ItemIds = itemIds
                };
            }

            if (shipmentIndex.Count > 0)
            {
                await _redisHash.SetAsync(hashKey, RedisHashFields.ShipmentsIndex, shipmentIndex, ttl);
            }

            if (shipmentEntries.Count > 0)
            {
                await _redisHash.SetManyAsync(hashKey, shipmentEntries, ttl);
            }

            var itemIndex = new List<string>();
            var itemEntries = new Dictionary<string, Item>(StringComparer.Ordinal);

            foreach (var shipment in shipments)
            {
                if (shipment?.Items == null) continue;

                foreach (var item in shipment.Items)
                {
                    if (string.IsNullOrWhiteSpace(item?.Id)) continue;

                    var field = GetItemField(item.Id);
                    if (itemEntries.ContainsKey(field)) continue;

                    itemEntries[field] = item;
                    itemIndex.Add(item.Id);
                }
            }

            if (itemIndex.Count > 0)
            {
                await _redisHash.SetAsync(hashKey, RedisHashFields.ItemsIndex, itemIndex, ttl);
            }

            if (itemEntries.Count > 0)
            {
                await _redisHash.SetManyAsync(hashKey, itemEntries, ttl);
            }
        }

        public Task InvalidateAsync(string quoteId)
        {
            return _redisHash.DeleteKeyAsync(GetHashKey(quoteId));
        }

        public async Task DeleteItemAsync(string quoteId, string itemId)
        {
            var hashKey = GetHashKey(quoteId);
            await _redisHash.DeleteFieldAsync(hashKey, GetItemField(itemId));

            var index = await _redisHash.GetAsync<List<string>>(hashKey, RedisHashFields.ItemsIndex);
            if (index == null) return;

            if (index.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal)) > 0)
            {
                await _redisHash.SetAsync(hashKey, RedisHashFields.ItemsIndex, index, GetPriceCacheTtl());
            }
        }

        public async Task DeleteShipmentAsync(string quoteId, string shipmentId)
        {
            var hashKey = GetHashKey(quoteId);
            await _redisHash.DeleteFieldAsync(hashKey, GetShipmentField(shipmentId));

            var index = await _redisHash.GetAsync<List<string>>(hashKey, RedisHashFields.ShipmentsIndex);
            if (index == null) return;

            if (index.RemoveAll(id => string.Equals(id, shipmentId, StringComparison.Ordinal)) > 0)
            {
                await _redisHash.SetAsync(hashKey, RedisHashFields.ShipmentsIndex, index, GetPriceCacheTtl());
            }
        }

        private string GetHashKey(string id)
        {
            return $"{QuotePriceHashKeyPrefix}:{id}:{_contextService?.UserContext?.CacheKey}";
        }

        private TimeSpan GetPriceCacheTtl()
        {
            return TimeSpan.FromSeconds(_applicationSettings.PriceCacheDuration);
        }

        private static string GetItemField(string itemId)
        {
            return $"item:{itemId}";
        }

        private static string GetShipmentField(string shipmentId)
        {
            return $"shipment:{shipmentId}";
        }
    }
}

class UnifiedBasketQuoteManager
{
    public async Task<byte[]> GetPricev2(string id, bool includeSkuTaxInfo = false, bool includeCategoryRollups = false, bool includeTaxTypeSplits = false)
        {
            byte[] prices = null;
            var cacheKey = string.Format(Constants.QuotePriceCacheKey, id, _contextService?.UserContext?.CacheKey);
            prices = await _quotePriceHashCache.GetFullAsync(id);
            if (prices != null) return prices;

            prices = await _cacheProvider.GetAsync<byte[]>(cacheKey);
            if (prices == null)
            {
                Basket basket = await _unifiedBasketRepository.GetBasketById<Basket>(id);
                IQuoteSalesOrderPrice quotePrice = await BasketMapper.Map(basket);

                if (quotePrice != null)
                {
                    prices = _cacheCompressor.Compress(quotePrice);
                    await _cacheProvider.SetAsync(cacheKey, prices,
                        new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                        {
                            AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(_applicationSettings.PriceCacheDuration)
                        });
                    await _quotePriceHashCache.SetAsync(id, quotePrice, prices);
                }
            }
            else
            {
                await _quotePriceHashCache.SetFullAsync(id, prices);
            }
            return prices;
        }
}