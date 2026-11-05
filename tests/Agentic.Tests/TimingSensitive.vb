Option Strict On
Option Explicit On
Option Infer On

Imports Xunit

''' <summary>
''' Tests that measure how long something takes ("answers within 1 s"). They run on their own, after the parallel ones, so a
''' busy machine (other tests starting Chrome, git or servers) cannot make them fail.
''' </summary>
<CollectionDefinition(TimingSensitive.Name, DisableParallelization:=True)>
Public NotInheritable Class TimingSensitive
    Public Const Name As String = "Timing-sensitive"
End Class
