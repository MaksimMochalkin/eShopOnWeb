using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Azure.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.Infrastructure.Identity;
using Microsoft.eShopWeb.Web.Interfaces;
using Newtonsoft.Json;
using OrderItemsDeliveryService;

namespace Microsoft.eShopWeb.Web.Pages.Basket;

[Authorize]
public class CheckoutModel : PageModel
{
    private readonly IBasketService _basketService;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IOrderService _orderService;
    private string _username = null;
    private readonly IBasketViewModelService _basketViewModelService;
    private readonly IAppLogger<CheckoutModel> _logger;

    public CheckoutModel(IBasketService basketService,
        IBasketViewModelService basketViewModelService,
        SignInManager<ApplicationUser> signInManager,
        IOrderService orderService,
        IAppLogger<CheckoutModel> logger)
    {
        _basketService = basketService;
        _signInManager = signInManager;
        _orderService = orderService;
        _basketViewModelService = basketViewModelService;
        _logger = logger;
    }

    public BasketViewModel BasketModel { get; set; } = new BasketViewModel();

    public async Task OnGet()
    {
        await SetBasketModelAsync();
    }

    public async Task<IActionResult> OnPost(IEnumerable<BasketItemViewModel> items, string shippingAddress)
    {
        try
        {
            await SetBasketModelAsync();

            if (!ModelState.IsValid)
            {
                return BadRequest();
            }

            var updateModel = items.ToDictionary(b => b.Id.ToString(), b => b.Quantity);
            await _basketService.SetQuantities(BasketModel.Id, updateModel);
            await _orderService.CreateOrderAsync(BasketModel.Id, new Address("123 Main St.", "Kent", "OH", "United States", "44240"));
            await _basketService.DeleteBasketAsync(BasketModel.Id);

            await SendDataIntoDeliveryOrderProcessor(shippingAddress, updateModel);

            await SendDataIntoServiceBus(updateModel);

        }
        catch (EmptyBasketOnCheckoutException emptyBasketOnCheckoutException)
        {
            //Redirect to Empty Basket page
            _logger.LogWarning(emptyBasketOnCheckoutException.Message);
            return RedirectToPage("/Basket/Index");
        }

        return RedirectToPage("Success");
    }

    private async Task SendDataIntoDeliveryOrderProcessor(string shippingAddress, Dictionary<string, int> updateModel)
    {
        var deliveryOrderProcessorUri = Environment.GetEnvironmentVariable("DeliveryOrderProcessorUri");
        var functionUrl = deliveryOrderProcessorUri + "/api/OrderItemsDeliveryServiceRun?";
        var functionClient = new HttpClient();
        var response = await functionClient.PostAsJsonAsync(functionUrl,
            new OrderDeliveryModel
            {
                Id = Guid.NewGuid().ToString(),
                ShippingAddress = shippingAddress,
                ListOfItems = updateModel,
                FinalPrice = BasketModel.Total()
            });
        await response.Content.ReadAsStringAsync();
    }

    private async Task SendDataIntoServiceBus(Dictionary<string, int> updateModel)
    {
        var msg = JsonConvert.SerializeObject(updateModel);
        await ServiceBusOrderSender(msg);

        var orderItemsReserverUri = Environment.GetEnvironmentVariable("OrderItemsReserverUri");
        var functionUrl = orderItemsReserverUri + "/api/ReservationOfOrderItems?";
        var functionClient = new HttpClient();
        var responce = await functionClient.PostAsJsonAsync(functionUrl, updateModel);
        await responce.Content.ReadAsStringAsync();
    }

    private async Task ServiceBusOrderSender(string msg)
    {
        var serviceBusConnectionString = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        var queueName = Environment.GetEnvironmentVariable("ServiceBusQueueName");
        var queueClient = new QueueClient(serviceBusConnectionString, queueName);

        try
        {
            var message = new Message(Encoding.UTF8.GetBytes(msg));
            await queueClient.SendAsync(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
        await queueClient.CloseAsync();
    }

    private async Task SetBasketModelAsync()
    {
        if (_signInManager.IsSignedIn(HttpContext.User))
        {
            BasketModel = await _basketViewModelService.GetOrCreateBasketForUser(User.Identity.Name);
        }
        else
        {
            GetOrSetBasketCookieAndUserName();
            BasketModel = await _basketViewModelService.GetOrCreateBasketForUser(_username);
        }
    }

    private void GetOrSetBasketCookieAndUserName()
    {
        if (Request.Cookies.ContainsKey(Constants.BASKET_COOKIENAME))
        {
            _username = Request.Cookies[Constants.BASKET_COOKIENAME];
        }
        if (_username != null) return;

        _username = Guid.NewGuid().ToString();
        var cookieOptions = new CookieOptions();
        cookieOptions.Expires = DateTime.Today.AddYears(10);
        Response.Cookies.Append(Constants.BASKET_COOKIENAME, _username, cookieOptions);
    }
}
