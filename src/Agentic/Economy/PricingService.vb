Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports Agentic.Configuration
Imports Agentic.Persistence

Namespace Economy

    Public NotInheritable Class ModelPrice
        Public Property ModelKey As String
        Public Property IsLocal As Boolean
        Public Property InputPerMillion As Decimal?
        Public Property OutputPerMillion As Decimal?
        Public Property PerImage As Decimal?
        Public Property Source As String
        Public Property EffectiveFromUtc As DateTime?

        ''' <summary>Local models are free; cloud models must have input and output prices.</summary>
        Public ReadOnly Property IsPriced As Boolean
            Get
                Return IsLocal OrElse (InputPerMillion.HasValue AndAlso OutputPerMillion.HasValue)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Model prices (USD per 1M tokens). Config provides defaults; admins can override from the dashboard
    ''' (stored with history in ModelPricing; the latest entry wins). Cost math is done in integer micro-USD:
    ''' tokens × (USD per 1M tokens) = micro-USD.
    ''' </summary>
    Public NotInheritable Class PricingService
        Public Const ImageGenerationKey As String = "image-generation"

        Private ReadOnly _db As Database
        Private ReadOnly _prices As New ConcurrentDictionary(Of String, ModelPrice)(StringComparer.OrdinalIgnoreCase)

        Public Sub New(db As Database, models As IEnumerable(Of ModelOptions), imageOptions As ImageGenerationOptions)
            _db = db
            For Each m In models
                _prices(m.Key) = New ModelPrice With {
                    .ModelKey = m.Key, .IsLocal = m.IsLocal, .InputPerMillion = m.Pricing?.InputPerMillion,
                    .OutputPerMillion = m.Pricing?.OutputPerMillion, .PerImage = m.Pricing?.PerImage, .Source = "config"}
            Next
            _prices(ImageGenerationKey) = New ModelPrice With {.ModelKey = ImageGenerationKey, .PerImage = imageOptions?.PricePerImage, .Source = "config"}
        End Sub

        Public Async Function LoadOverridesAsync() As Task
            Dim rows = Await _db.QueryAsync(
                "SELECT p.* FROM ModelPricing p JOIN (SELECT ModelKey, MAX(Id) AS MaxId FROM ModelPricing GROUP BY ModelKey) l ON p.Id = l.MaxId",
                Function(r) New ModelPrice With {
                    .ModelKey = Db.Str(r, "ModelKey"), .InputPerMillion = Db.Dec(r, "InputPerMillion"), .OutputPerMillion = Db.Dec(r, "OutputPerMillion"),
                    .PerImage = Db.Dec(r, "PerImage"), .Source = "admin:" & Db.Str(r, "SetBy"), .EffectiveFromUtc = Db.DtN(r, "EffectiveFromUtc")}).ConfigureAwait(False)
            For Each p In rows
                Dim existing As ModelPrice = Nothing
                ' Only known models (config, image generation, registered custom models); stale history is ignored.
                If Not _prices.TryGetValue(p.ModelKey, existing) Then Continue For
                p.IsLocal = existing.IsLocal
                _prices(p.ModelKey) = p
            Next
        End Function

        ''' <summary>Adds a runtime-registered (custom) model's price.</summary>
        Public Sub RegisterModel(modelKey As String, isLocal As Boolean, inputPerMillion As Decimal?, outputPerMillion As Decimal?, perImage As Decimal?)
            _prices(modelKey) = New ModelPrice With {
                .ModelKey = modelKey, .IsLocal = isLocal, .InputPerMillion = inputPerMillion, .OutputPerMillion = outputPerMillion, .PerImage = perImage, .Source = "custom"}
        End Sub

        Public Sub Unregister(modelKey As String)
            Dim removed As ModelPrice = Nothing
            If modelKey IsNot Nothing Then _prices.TryRemove(modelKey, removed)
        End Sub

        Public Function GetPrice(modelKey As String) As ModelPrice
            Dim p As ModelPrice = Nothing
            If modelKey IsNot Nothing AndAlso _prices.TryGetValue(modelKey, p) Then Return p
            Return New ModelPrice With {.ModelKey = modelKey, .Source = "none"}
        End Function

        Public Function All() As List(Of ModelPrice)
            Return _prices.Values.OrderBy(Function(p) p.ModelKey).ToList()
        End Function

        Public Async Function SetPriceAsync(modelKey As String, inputPerMillion As Decimal?, outputPerMillion As Decimal?, perImage As Decimal?, actor As String) As Task
            If inputPerMillion.HasValue AndAlso inputPerMillion.Value < 0 OrElse outputPerMillion.HasValue AndAlso outputPerMillion.Value < 0 OrElse perImage.HasValue AndAlso perImage.Value < 0 Then
                Throw New ArgumentException("Prices must be >= 0.")
            End If
            Dim existing = GetPrice(modelKey)
            If existing.Source = "none" Then Throw New KeyNotFoundException($"Unknown model '{modelKey}'.")
            Dim now = DateTime.UtcNow
            Await _db.ExecuteAsync("INSERT INTO ModelPricing(ModelKey, InputPerMillion, OutputPerMillion, PerImage, EffectiveFromUtc, SetBy) VALUES (@m, @i, @o, @p, @t, @by)",
                                   ("@m", CObj(modelKey)), ("@i", If(inputPerMillion.HasValue, CObj(inputPerMillion.Value), Nothing)),
                                   ("@o", If(outputPerMillion.HasValue, CObj(outputPerMillion.Value), Nothing)), ("@p", If(perImage.HasValue, CObj(perImage.Value), Nothing)),
                                   ("@t", Db.Ts(now)), ("@by", actor)).ConfigureAwait(False)
            _prices(modelKey) = New ModelPrice With {
                .ModelKey = modelKey, .IsLocal = existing.IsLocal, .InputPerMillion = inputPerMillion, .OutputPerMillion = outputPerMillion,
                .PerImage = perImage, .Source = "admin:" & actor, .EffectiveFromUtc = now}
        End Function

        ''' <summary>tokens × (USD per 1M) = micro-USD, rounded up; cachedTokens (part of promptTokens, served from the provider's cache) cost a tenth of the input price.</summary>
        Public Shared Function CostMicros(price As ModelPrice, promptTokens As Long, completionTokens As Long, Optional cachedTokens As Long = 0) As Long
            If price Is Nothing OrElse price.IsLocal Then Return 0
            Dim prompt = Math.Max(0L, promptTokens), cached = Math.Min(Math.Max(0L, promptTokens), Math.Max(0L, cachedTokens))
            Dim cost = CDec(prompt - cached) * price.InputPerMillion.GetValueOrDefault() + CDec(cached) * price.InputPerMillion.GetValueOrDefault() * AccountService.DefaultCachedFraction + CDec(Math.Max(0, completionTokens)) * price.OutputPerMillion.GetValueOrDefault()
            Return CLng(Math.Ceiling(cost))
        End Function

        Public Shared Function ImageCostMicros(price As ModelPrice, images As Integer) As Long
            If price Is Nothing OrElse Not price.PerImage.HasValue Then Return 0
            Return CLng(Math.Ceiling(price.PerImage.Value * 1000000D * images))
        End Function
    End Class

End Namespace
