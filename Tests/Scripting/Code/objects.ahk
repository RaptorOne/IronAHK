a =b
b :={ "one" : 1, two=2
	,	"three" =["pass", 4,{ "x", "y" = "!", "a": "b{c}"}] }
b["three"][2]["y"] := "o"

if (b.one != 1)
	FileAppend, fail1, *

if (%a%["tw" . b.three[2]["y"]] != 2)
	FileAppend, fail2, *

if ({ a : { b : "c" } }.a["b"] != "c")
	FileAppend, fail3, *

if (([1,2,3][1] += 3) != 5)
	FileAppend, fail4, *

c := { }
c["x"]["y"] := "z"
if (c.x.y != "z")
	FileAppend, fail, *

FileAppend, % %a%["three"][0], *
