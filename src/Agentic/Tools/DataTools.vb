Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Work

Namespace Tools

    ''' <summary>market_prices: crypto prices and exchange rates the Money tab keeps (every agent; read only).</summary>
    Public NotInheritable Class MarketPricesTool
        Inherits InternalTool

        Private ReadOnly _markets As MarketsService

        Public Sub New(markets As MarketsService)
            _markets = markets
        End Sub

        Public Overrides ReadOnly Property Name As String = MarketsService.ToolName
        Public Overrides ReadOnly Property Description As String = MarketsService.ToolDescription
        Public Overrides ReadOnly Property Parameters As JsonObject = JsonNode.Parse(MarketsService.ToolSchema).AsObject()

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Return ToolResult.Ok(Await _markets.ToolAnswerAsync(args.GetRawText()).ConfigureAwait(False))
        End Function
    End Class

    ''' <summary>stock_quotes: stock and index quotes (Yahoo Finance) for any ticker, the Stocks tab's data (every agent; read only).</summary>
    Public NotInheritable Class StockQuotesTool
        Inherits InternalTool

        Private ReadOnly _stocks As StocksService

        Public Sub New(stocks As StocksService)
            _stocks = stocks
        End Sub

        Public Overrides ReadOnly Property Name As String = StocksService.ToolName
        Public Overrides ReadOnly Property Description As String = StocksService.ToolDescription
        Public Overrides ReadOnly Property Parameters As JsonObject = JsonNode.Parse(StocksService.ToolSchema).AsObject()

        Public Overrides ReadOnly Property Timeout As TimeSpan
            Get
                Return TimeSpan.FromSeconds(60)
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Return ToolResult.Ok(Await _stocks.ToolAnswerAsync(args.GetRawText(), ctx.CancellationToken).ConfigureAwait(False))
        End Function
    End Class

    ''' <summary>polymarket: Polymarket's prediction markets (odds, live prices, trades, holders, wallets), the Polymarket tab's data (every agent; read only).</summary>
    Public NotInheritable Class PolymarketTool
        Inherits InternalTool

        Private ReadOnly _polymarket As PolymarketService

        Public Sub New(polymarket As PolymarketService)
            _polymarket = polymarket
        End Sub

        Public Overrides ReadOnly Property Name As String = PolymarketService.ToolName
        Public Overrides ReadOnly Property Description As String = PolymarketService.ToolDescription
        Public Overrides ReadOnly Property Parameters As JsonObject = JsonNode.Parse(PolymarketService.ToolSchema).AsObject()

        Public Overrides ReadOnly Property Timeout As TimeSpan
            Get
                Return TimeSpan.FromSeconds(60)
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Return ToolResult.Ok(Await _polymarket.ToolAnswerAsync(args.GetRawText(), ctx.CancellationToken).ConfigureAwait(False))
        End Function
    End Class

    ''' <summary>news_articles: recent articles the News tab collects from RSS feeds (every agent; read only, untrusted text).</summary>
    Public NotInheritable Class NewsArticlesTool
        Inherits InternalTool

        Private ReadOnly _news As NewsService

        Public Sub New(news As NewsService)
            _news = news
        End Sub

        Public Overrides ReadOnly Property Name As String = NewsService.ToolName
        Public Overrides ReadOnly Property Description As String = NewsService.ToolDescription
        Public Overrides ReadOnly Property Parameters As JsonObject = JsonNode.Parse(NewsService.ToolSchema).AsObject()

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim list = Await _news.ToolSearchAsync(args.GetRawText()).ConfigureAwait(False)
            Return New ToolResult With {.Success = True, .Untrusted = True, .UntrustedSource = "news-feeds", .Content = NewsService.ToolText(list, 1)}
        End Function
    End Class

End Namespace
