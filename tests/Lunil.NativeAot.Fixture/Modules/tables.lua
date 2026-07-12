local table = { value = 40, [1] = 1 }
table.value = table.value + table[1] + 1
return table.value
